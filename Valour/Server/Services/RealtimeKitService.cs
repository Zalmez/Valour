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
        string displayName)
    {
        if (!IsConfigured)
        {
            return TaskResult<RealtimeKitVoiceTokenResponse>.FromFailure(
                "RealtimeKit is not configured on the server.");
        }

        var meetingResult = await GetOrCreateMeetingIdAsync(channel);
        if (!meetingResult.Success)
            return TaskResult<RealtimeKitVoiceTokenResponse>.FromFailure(meetingResult);

        // Always kick any existing participant for this user before adding a new one
        var customParticipantId = userId.ToString();
        await KickParticipantFromMeetingAsync(meetingResult.Data, customParticipantId);

        var participantResult = await AddParticipantAsync(
            meetingResult.Data,
            customParticipantId,
            displayName,
            $"{{\"channelId\":\"{channel.Id}\",\"userId\":\"{userId}\"}}",
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

    /// <summary>
    /// Kicks a participant from the active session of a meeting via the Cloudflare API.
    /// Failures are logged but not thrown — this is best-effort cleanup.
    /// </summary>
    private async Task KickParticipantFromMeetingAsync(string meetingId, string customParticipantId)
    {
        try
        {
            var endpoint = BuildEndpoint($"meetings/{meetingId}/active-session/kick");
            var payload = new KickParticipantRequest
            {
                CustomParticipantIds = new[] { customParticipantId }
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
                    "Kick participant {ParticipantId} from meeting {MeetingId} returned {Status}: {Body}",
                    customParticipantId, meetingId, (int)response.StatusCode, body);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to kick participant {ParticipantId} from meeting {MeetingId}",
                customParticipantId, meetingId);
        }
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

            return long.TryParse(CustomParticipantId, out var userId) ? userId : null;
        }
    }
}
