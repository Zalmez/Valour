using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Valour.Config.Configs;
using Valour.Shared;
using Valour.Shared.Models;

namespace Valour.Server.Services;

public class RealtimeKitService
{
    private const string CloudflareApiBase = "https://api.cloudflare.com/client/v4";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<RealtimeKitService> _logger;

    private readonly ConcurrentDictionary<long, string> _meetingIdsByChannel = new();
    private readonly ConcurrentDictionary<long, SemaphoreSlim> _channelLocks = new();
    private readonly ConcurrentDictionary<long, SemaphoreSlim> _userJoinLocks = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public RealtimeKitService(
        IHttpClientFactory httpClientFactory,
        ILogger<RealtimeKitService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    private static bool IsConfigured =>
        !string.IsNullOrWhiteSpace(CloudflareConfig.Instance?.RealtimeAccountId) &&
        !string.IsNullOrWhiteSpace(CloudflareConfig.Instance?.RealtimeAppId) &&
        !string.IsNullOrWhiteSpace(CloudflareConfig.Instance?.RealtimeApiToken);

    private static string VoicePresetName =>
        string.IsNullOrWhiteSpace(CloudflareConfig.Instance?.RealtimePresetName)
            ? "group_call_host"
            : CloudflareConfig.Instance.RealtimePresetName;

    private static string VideoPresetName =>
        string.IsNullOrWhiteSpace(CloudflareConfig.Instance?.RealtimeVideoPresetName)
            ? "video_call"
            : CloudflareConfig.Instance.RealtimeVideoPresetName;

    public async Task<TaskResult<RealtimeKitVoiceTokenResponse>> CreateParticipantTokenAsync(
        Channel channel,
        long userId,
        string displayName,
        string? sessionId)
    {
        if (!IsConfigured)
        {
            return TaskResult<RealtimeKitVoiceTokenResponse>.FromFailure(
                "RealtimeKit is not configured on the server.");
        }

        var userJoinLock = _userJoinLocks.GetOrAdd(userId, static _ => new SemaphoreSlim(1, 1));
        await userJoinLock.WaitAsync();

        try
        {
            var meetingResult = await GetOrCreateMeetingIdAsync(channel);
            if (!meetingResult.Success)
                return TaskResult<RealtimeKitVoiceTokenResponse>.FromFailure(meetingResult);

            // Always kick any existing participant sessions for this user in the same meeting before adding a new one.
            await KickUserFromMeetingAsync(meetingResult.Data, userId);

            var customParticipantId = BuildCustomParticipantId(userId, sessionId);

            var participantResult = await AddParticipantAsync(
                meetingResult.Data,
                customParticipantId,
                displayName,
                BuildParticipantMetadata(channel.Id, userId, sessionId),
                channel.ChannelType);

            if (!participantResult.Success)
                return TaskResult<RealtimeKitVoiceTokenResponse>.FromFailure(participantResult);

            return TaskResult<RealtimeKitVoiceTokenResponse>.FromData(new RealtimeKitVoiceTokenResponse
            {
                MeetingId = meetingResult.Data,
                ParticipantId = participantResult.Data.ParticipantId,
                AuthToken = participantResult.Data.AuthToken
            });
        }
        finally
        {
            userJoinLock.Release();
        }
    }

    private async Task<TaskResult<string>> GetOrCreateMeetingIdAsync(Channel channel)
    {
        if (_meetingIdsByChannel.TryGetValue(channel.Id, out var existingId))
        {
            return TaskResult<string>.FromData(existingId);
        }

        var channelLock = _channelLocks.GetOrAdd(channel.Id, static _ => new SemaphoreSlim(1, 1));
        await channelLock.WaitAsync();

        try
        {
            if (_meetingIdsByChannel.TryGetValue(channel.Id, out existingId))
            {
                return TaskResult<string>.FromData(existingId);
            }

            var createResult = await CreateMeetingAsync(channel);
            if (!createResult.Success)
                return createResult;

            _meetingIdsByChannel[channel.Id] = createResult.Data;
            return createResult;
        }
        finally
        {
            channelLock.Release();
        }
    }

    private async Task<TaskResult<string>> CreateMeetingAsync(Channel channel)
    {
        var endpoint = BuildEndpoint("meetings");
        var payload = new CreateMeetingRequest
        {
            Title = $"{channel.Name} ({channel.Id})",
            Metadata = $"channel:{channel.Id}"
        };

        var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(payload)
        };

        request.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", CloudflareConfig.Instance.RealtimeApiToken);

        return await SendAsync<CloudflareMeetingResult, string>(
            request,
            data => data.Id,
            "create meeting");
    }

    private async Task<TaskResult<ParticipantTokenResult>> AddParticipantAsync(
        string meetingId,
        string customParticipantId,
        string displayName,
        string metadata,
        ChannelTypeEnum channelType)
    {
        var endpoint = BuildEndpoint($"meetings/{meetingId}/participants");
        var presetName = channelType == ChannelTypeEnum.PlanetVideo ? VideoPresetName : VoicePresetName;
        var payload = new AddParticipantRequest
        {
            PresetName = presetName,
            CustomParticipantId = customParticipantId,
            Name = displayName,
            Metadata = metadata
        };

        var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(payload)
        };

        request.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", CloudflareConfig.Instance.RealtimeApiToken);

        return await SendAsync<CloudflareParticipantResult, ParticipantTokenResult>(
            request,
            data => new ParticipantTokenResult
            {
                ParticipantId = data.Id,
                AuthToken = data.Token
            },
            "add participant");
    }

    private static string BuildCustomParticipantId(long userId, string? sessionId)
    {
        var normalizedSessionId = NormalizeSessionId(sessionId);
        return string.IsNullOrWhiteSpace(normalizedSessionId)
            ? userId.ToString()
            : $"{userId}:{normalizedSessionId}";
    }

    private static string NormalizeSessionId(string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return string.Empty;

        // Preserve a stable userId:sessionId format for downstream parsing.
        return sessionId.Trim().Replace(':', '_');
    }

    private static string BuildParticipantMetadata(long channelId, long userId, string? sessionId)
    {
        var normalizedSessionId = NormalizeSessionId(sessionId);
        return string.IsNullOrWhiteSpace(normalizedSessionId)
            ? $"{{\"channelId\":\"{channelId}\",\"userId\":\"{userId}\"}}"
            : $"{{\"channelId\":\"{channelId}\",\"userId\":\"{userId}\",\"sessionId\":\"{normalizedSessionId}\"}}";
    }

    /// <summary>
    /// Best-effort cleanup: kicks all RTK sessions for a user in a tracked channel.
    /// </summary>
    public async Task KickUserFromTrackedChannelAsync(long channelId, long userId)
    {
        if (!_meetingIdsByChannel.TryGetValue(channelId, out var meetingId) || string.IsNullOrWhiteSpace(meetingId))
            return;

        await KickUserFromMeetingAsync(meetingId, userId);
    }

    /// <summary>
    /// Best-effort cleanup: kicks the exact RTK session for a user when a sessionId is known.
    /// Falls back to kicking all user sessions in the channel when no sessionId is provided.
    /// </summary>
    public async Task KickUserSessionFromTrackedChannelAsync(long channelId, long userId, string? sessionId)
    {
        if (!_meetingIdsByChannel.TryGetValue(channelId, out var meetingId) || string.IsNullOrWhiteSpace(meetingId))
            return;

        var normalizedSessionId = NormalizeSessionId(sessionId);
        if (string.IsNullOrWhiteSpace(normalizedSessionId))
        {
            await KickUserFromMeetingAsync(meetingId, userId);
            return;
        }

        await KickParticipantFromMeetingAsync(meetingId, BuildCustomParticipantId(userId, normalizedSessionId));
    }

    /// <summary>
    /// Kicks all active RTK participants for the specified user from a meeting (across all live sessions).
    /// Failures are logged but not thrown — this is best-effort cleanup.
    /// </summary>
    private async Task KickUserFromMeetingAsync(string meetingId, long userId)
    {
        try
        {
            var sessionsResult = await GetLiveSessionsForMeetingAsync(meetingId);
            if (!sessionsResult.Success || sessionsResult.Data is null)
                return;

            var customParticipantIds = new HashSet<string>(StringComparer.Ordinal);

            foreach (var session in sessionsResult.Data)
            {
                if (session is null || string.IsNullOrWhiteSpace(session.Id) || session.LiveParticipants <= 0)
                    continue;

                var participantsResult = await GetSessionParticipantsAsync(session.Id);
                if (!participantsResult.Success || participantsResult.Data is null)
                    continue;

                foreach (var participant in participantsResult.Data)
                {
                    if (participant.ExtractUserId() != userId)
                        continue;

                    if (!string.IsNullOrWhiteSpace(participant.CustomParticipantId))
                        customParticipantIds.Add(participant.CustomParticipantId);
                }
            }

            if (customParticipantIds.Count == 0)
                return;

            await KickParticipantsFromMeetingAsync(meetingId, customParticipantIds);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to kick existing participant sessions for user {UserId} from meeting {MeetingId}",
                userId,
                meetingId);
        }
    }

    /// <summary>
    /// Kicks one or more participants from the active session of a meeting via the Cloudflare API.
    /// Failures are logged but not thrown — this is best-effort cleanup.
    /// </summary>
    private async Task KickParticipantsFromMeetingAsync(string meetingId, IEnumerable<string> customParticipantIds)
    {
        var ids = customParticipantIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (ids.Length == 0)
            return;

        try
        {
            var endpoint = BuildEndpoint($"meetings/{meetingId}/active-session/kick");
            var payload = new KickParticipantRequest
            {
                CustomParticipantIds = ids
            };

            var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = JsonContent.Create(payload)
            };

            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", CloudflareConfig.Instance.RealtimeApiToken);

            using var client = _httpClientFactory.CreateClient();
            using var response = await client.SendAsync(request);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                _logger.LogDebug(
                    "Kick participants {ParticipantIds} from meeting {MeetingId} returned {Status}: {Body}",
                    string.Join(", ", ids), meetingId, (int)response.StatusCode, body);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to kick participants from meeting {MeetingId}: {ParticipantIds}",
                meetingId, string.Join(", ", ids));
        }
    }

    private Task KickParticipantFromMeetingAsync(string meetingId, string customParticipantId)
    {
        return KickParticipantsFromMeetingAsync(meetingId, new[] { customParticipantId });
    }

    /// <summary>
    /// Returns a snapshot of all channel → meeting ID mappings currently tracked.
    /// </summary>
    public Dictionary<long, string> GetTrackedChannelMeetingIds()
    {
        return new Dictionary<long, string>(_meetingIdsByChannel);
    }

    /// <summary>
    /// Removes a channel → meeting ID mapping (e.g. when the meeting is no longer active).
    /// </summary>
    public void RemoveMeetingMapping(long channelId)
    {
        _meetingIdsByChannel.TryRemove(channelId, out _);
    }

    /// <summary>
    /// Fetches all LIVE sessions for a specific meeting from Cloudflare.
    /// </summary>
    public async Task<TaskResult<List<CloudflareSessionInfo>>> GetLiveSessionsForMeetingAsync(string meetingId)
    {
        if (!IsConfigured)
            return TaskResult<List<CloudflareSessionInfo>>.FromFailure("RealtimeKit is not configured.");

        var endpoint = BuildEndpoint($"sessions?associated_id={meetingId}&status=LIVE&per_page=100");
        var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", CloudflareConfig.Instance.RealtimeApiToken);

        return await SendAsync<CloudflareSessionsListResult, List<CloudflareSessionInfo>>(
            request,
            data => data.Sessions ?? new List<CloudflareSessionInfo>(),
            "list live sessions");
    }

    /// <summary>
    /// Fetches all participants for a specific session from Cloudflare.
    /// </summary>
    public async Task<TaskResult<List<CloudflareSessionParticipantInfo>>> GetSessionParticipantsAsync(string sessionId)
    {
        if (!IsConfigured)
            return TaskResult<List<CloudflareSessionParticipantInfo>>.FromFailure("RealtimeKit is not configured.");

        var endpoint = BuildEndpoint($"sessions/{sessionId}/participants?per_page=500");
        var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", CloudflareConfig.Instance.RealtimeApiToken);

        return await SendAsync<CloudflareSessionParticipantsResult, List<CloudflareSessionParticipantInfo>>(
            request,
            data => data.Participants ?? new List<CloudflareSessionParticipantInfo>(),
            "list session participants");
    }

    private async Task<TaskResult<TOut>> SendAsync<TCloudflare, TOut>(
        HttpRequestMessage request,
        Func<TCloudflare, TOut> map,
        string operation)
        where TCloudflare : class
    {
        try
        {
            using (request)
            {
                using var client = _httpClientFactory.CreateClient();
                using var response = await client.SendAsync(request);
                var body = await response.Content.ReadAsStringAsync();

                CloudflareResponse<TCloudflare>? wrapper = null;

                if (!string.IsNullOrWhiteSpace(body))
                {
                    wrapper = JsonSerializer.Deserialize<CloudflareResponse<TCloudflare>>(body, JsonOptions);
                }

                if (!response.IsSuccessStatusCode)
                {
                    var message = GetErrorMessage(wrapper, body);
                    _logger.LogWarning(
                        "Cloudflare RealtimeKit failed to {Operation}. Status: {Status}. Message: {Message}",
                        operation,
                        (int)response.StatusCode,
                        message);

                    return TaskResult<TOut>.FromFailure($"Failed to {operation}: {message}", (int)response.StatusCode);
                }

                if (wrapper?.Success != true || wrapper.Result is null)
                {
                    var message = GetErrorMessage(wrapper, body);
                    return TaskResult<TOut>.FromFailure($"Failed to {operation}: {message}");
                }

                return TaskResult<TOut>.FromData(map(wrapper.Result));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Cloudflare RealtimeKit request failed while trying to {Operation}", operation);
            return TaskResult<TOut>.FromFailure(ex);
        }
    }

    private static string BuildEndpoint(string relativePath)
    {
        return
            $"{CloudflareApiBase}/accounts/{CloudflareConfig.Instance.RealtimeAccountId}/realtime/kit/{CloudflareConfig.Instance.RealtimeAppId}/{relativePath}";
    }

    private static string GetErrorMessage<T>(CloudflareResponse<T>? response, string rawBody)
    {
        var cloudflareError = response?.Errors?.FirstOrDefault();
        if (cloudflareError is not null)
        {
            return $"{cloudflareError.Code}: {cloudflareError.Message}";
        }

        return string.IsNullOrWhiteSpace(rawBody) ? "Unknown error" : rawBody;
    }

    private sealed class CreateMeetingRequest
    {
        [JsonPropertyName("title")]
        public string Title { get; set; } = string.Empty;

        [JsonPropertyName("metadata")]
        public string Metadata { get; set; } = string.Empty;
    }

    private sealed class AddParticipantRequest
    {
        [JsonPropertyName("preset_name")]
        public string PresetName { get; set; } = string.Empty;

        [JsonPropertyName("custom_participant_id")]
        public string CustomParticipantId { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("metadata")]
        public string Metadata { get; set; } = string.Empty;
    }

    private sealed class KickParticipantRequest
    {
        [JsonPropertyName("custom_participant_ids")]
        public string[] CustomParticipantIds { get; set; } = Array.Empty<string>();
    }

    private sealed class CloudflareMeetingResult
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;
    }

    private sealed class CloudflareParticipantResult
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("token")]
        public string Token { get; set; } = string.Empty;
    }

    private sealed class ParticipantTokenResult
    {
        public string ParticipantId { get; set; } = string.Empty;
        public string AuthToken { get; set; } = string.Empty;
    }

    private sealed class CloudflareResponse<T>
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("data")]
        public T? Result { get; set; }

        [JsonPropertyName("errors")]
        public CloudflareError[]? Errors { get; set; }
    }

    private sealed class CloudflareError
    {
        [JsonPropertyName("code")]
        public int Code { get; set; }

        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;
    }

    private sealed class CloudflareSessionsListResult
    {
        [JsonPropertyName("sessions")]
        public List<CloudflareSessionInfo>? Sessions { get; set; }
    }

    private sealed class CloudflareSessionParticipantsResult
    {
        [JsonPropertyName("participants")]
        public List<CloudflareSessionParticipantInfo>? Participants { get; set; }
    }

    public sealed class CloudflareSessionInfo
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("associated_id")]
        public string AssociatedId { get; set; } = string.Empty;

        [JsonPropertyName("status")]
        public string Status { get; set; } = string.Empty;

        [JsonPropertyName("live_participants")]
        public int LiveParticipants { get; set; }
    }

    public sealed class CloudflareSessionParticipantInfo
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("custom_participant_id")]
        public string CustomParticipantId { get; set; } = string.Empty;

        [JsonPropertyName("left_at")]
        public string? LeftAt { get; set; }

        public long? ExtractUserId()
        {
            if (string.IsNullOrEmpty(CustomParticipantId))
                return null;

            var delimiterIndex = CustomParticipantId.IndexOf(':');
            var candidate = delimiterIndex > 0 ? CustomParticipantId[..delimiterIndex] : CustomParticipantId;
            return long.TryParse(candidate, out var userId) ? userId : null;
        }
    }
}
