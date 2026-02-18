using Valour.Shared;
using Valour.Shared.Authorization;
using Valour.Shared.Models;

namespace Valour.Server.API;

public class VoiceSignallingApi
{
    public static void AddRoutes(WebApplication app)
    {
        app.MapPost("api/voice/realtimekit/token/{channelId:long}", GetRealtimeKitToken);
        app.MapPost("api/voice/realtimekit/channels/{channelId:long}/participants/{targetUserId:long}/mute", MuteParticipant);
        app.MapPost("api/voice/realtimekit/channels/{channelId:long}/participants/{targetUserId:long}/unmute", UnmuteParticipant);
        app.MapPost("api/voice/realtimekit/channels/{channelId:long}/participants/{targetUserId:long}/kick", KickParticipant);
    }

    public static async Task<IResult> GetRealtimeKitToken(
        ValourDb db,
        TokenService tokenService,
        PlanetMemberService memberService,
        CoreHubService coreHubService,
        RealtimeKitService realtimeKitService,
        long channelId,
        string? sessionId)
    {
        var authToken = await tokenService.GetCurrentTokenAsync();
        if (authToken is null)
            return ValourResult.InvalidToken();

        var dbChannel = await db.Channels.FindAsync(channelId);
        if (dbChannel is null || !ISharedChannel.VoiceChannelTypes.Contains(dbChannel.ChannelType))
            return ValourResult.NotFound("Channel does not exist.");

        // Planet voice is the only supported type in this migration pass.
        if (dbChannel.ChannelType != ChannelTypeEnum.PlanetVoice || dbChannel.PlanetId is null)
        {
            return ValourResult.BadRequest("RealtimeKit voice currently supports only planet voice channels.");
        }

        var channel = dbChannel.ToModel();

        var member = await memberService.GetByUserAsync(authToken.UserId, dbChannel.PlanetId.Value);
        if (member is null)
            return ValourResult.NotPlanetMember();

        var hasViewPermission = await memberService.HasPermissionAsync(member, channel, VoiceChannelPermissions.View);
        if (!hasViewPermission)
            return ValourResult.LacksPermission(VoiceChannelPermissions.View);

        var hasJoinPermission = await memberService.HasPermissionAsync(member, channel, VoiceChannelPermissions.Join);
        if (!hasJoinPermission)
            return ValourResult.LacksPermission(VoiceChannelPermissions.Join);

        var dbUser = await db.Users.FindAsync(authToken.UserId);
        if (dbUser is null)
            return ValourResult.NotFound("User was not found.");

        var displayName = $"{dbUser.Name}#{dbUser.Tag}";
        TaskResult<RealtimeKitVoiceTokenResponse> tokenResult =
            await realtimeKitService.CreateParticipantTokenAsync(channel, authToken.UserId, displayName);

        if (!tokenResult.Success || tokenResult.Data is null)
        {
            return ValourResult.BadRequest(tokenResult.Message);
        }

        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            coreHubService.NotifyVoiceSessionReplace(authToken.UserId, new VoiceSessionReplaceEvent
            {
                ChannelId = channel.Id,
                SessionId = sessionId
            });
        }

        return ValourResult.Json(tokenResult.Data);
    }

    public static async Task<IResult> MuteParticipant(
        ValourDb db,
        TokenService tokenService,
        PlanetMemberService memberService,
        NodeLifecycleService nodeLifecycleService,
        long channelId,
        long targetUserId)
    {
        var validation = await ValidateModerationRequestAsync(
            db,
            tokenService,
            memberService,
            channelId,
            targetUserId,
            VoiceChannelPermissions.MuteMembers);

        if (validation.Error is not null)
            return validation.Error;

        await nodeLifecycleService.RelayUserEventAsync(
            validation.TargetMember!.UserId,
            NodeLifecycleService.NodeEventType.VoiceModeration,
            new VoiceModerationEvent
            {
                ChannelId = validation.Channel!.Id,
                ModeratorUserId = validation.ActorMember!.UserId,
                TargetUserId = validation.TargetMember.UserId,
                Action = VoiceModerationActionType.Mute
            });

        return Results.Ok();
    }

    public static async Task<IResult> UnmuteParticipant(
        ValourDb db,
        TokenService tokenService,
        PlanetMemberService memberService,
        NodeLifecycleService nodeLifecycleService,
        long channelId,
        long targetUserId)
    {
        var validation = await ValidateModerationRequestAsync(
            db,
            tokenService,
            memberService,
            channelId,
            targetUserId,
            VoiceChannelPermissions.MuteMembers);

        if (validation.Error is not null)
            return validation.Error;

        await nodeLifecycleService.RelayUserEventAsync(
            validation.TargetMember!.UserId,
            NodeLifecycleService.NodeEventType.VoiceModeration,
            new VoiceModerationEvent
            {
                ChannelId = validation.Channel!.Id,
                ModeratorUserId = validation.ActorMember!.UserId,
                TargetUserId = validation.TargetMember.UserId,
                Action = VoiceModerationActionType.Unmute
            });

        return Results.Ok();
    }

    public static async Task<IResult> KickParticipant(
        ValourDb db,
        TokenService tokenService,
        PlanetMemberService memberService,
        NodeLifecycleService nodeLifecycleService,
        long channelId,
        long targetUserId)
    {
        var validation = await ValidateModerationRequestAsync(
            db,
            tokenService,
            memberService,
            channelId,
            targetUserId,
            VoiceChannelPermissions.KickMembers);

        if (validation.Error is not null)
            return validation.Error;

        await nodeLifecycleService.RelayUserEventAsync(
            validation.TargetMember!.UserId,
            NodeLifecycleService.NodeEventType.VoiceModeration,
            new VoiceModerationEvent
            {
                ChannelId = validation.Channel!.Id,
                ModeratorUserId = validation.ActorMember!.UserId,
                TargetUserId = validation.TargetMember.UserId,
                Action = VoiceModerationActionType.Kick
            });

        return Results.Ok();
    }

    private static async Task<VoiceModerationValidationResult> ValidateModerationRequestAsync(
        ValourDb db,
        TokenService tokenService,
        PlanetMemberService memberService,
        long channelId,
        long targetUserId,
        VoiceChannelPermission requiredPermission)
    {
        var authToken = await tokenService.GetCurrentTokenAsync();
        if (authToken is null)
            return VoiceModerationValidationResult.FromError(ValourResult.InvalidToken());

        var dbChannel = await db.Channels.FindAsync(channelId);
        if (dbChannel is null || !ISharedChannel.VoiceChannelTypes.Contains(dbChannel.ChannelType))
            return VoiceModerationValidationResult.FromError(ValourResult.NotFound("Channel does not exist."));

        if (dbChannel.ChannelType != ChannelTypeEnum.PlanetVoice || dbChannel.PlanetId is null)
        {
            return VoiceModerationValidationResult.FromError(
                ValourResult.BadRequest("Voice moderation currently supports only planet voice channels."));
        }

        var channel = dbChannel.ToModel();

        var actor = await memberService.GetByUserAsync(authToken.UserId, dbChannel.PlanetId.Value);
        if (actor is null)
            return VoiceModerationValidationResult.FromError(ValourResult.NotPlanetMember());

        if (!await memberService.HasPermissionAsync(actor, channel, VoiceChannelPermissions.View))
            return VoiceModerationValidationResult.FromError(ValourResult.LacksPermission(VoiceChannelPermissions.View));

        if (!await memberService.HasPermissionAsync(actor, channel, requiredPermission))
            return VoiceModerationValidationResult.FromError(ValourResult.LacksPermission(requiredPermission));

        var target = await memberService.GetByUserAsync(targetUserId, dbChannel.PlanetId.Value);
        if (target is null)
            return VoiceModerationValidationResult.FromError(ValourResult.NotFound("Target user is not a planet member."));

        if (target.UserId == actor.UserId)
            return VoiceModerationValidationResult.FromError(ValourResult.BadRequest("You cannot moderate yourself."));

        var actorAuthority = await memberService.GetAuthorityAsync(actor);
        var targetAuthority = await memberService.GetAuthorityAsync(target);
        if (targetAuthority >= actorAuthority)
        {
            return VoiceModerationValidationResult.FromError(
                ValourResult.Forbid("The target has equal or higher authority than you."));
        }

        return new VoiceModerationValidationResult
        {
            Channel = channel,
            ActorMember = actor,
            TargetMember = target
        };
    }

    private sealed class VoiceModerationValidationResult
    {
        public IResult? Error { get; set; }

        public Channel? Channel { get; set; }

        public PlanetMember? ActorMember { get; set; }

        public PlanetMember? TargetMember { get; set; }

        public static VoiceModerationValidationResult FromError(IResult error) => new()
        {
            Error = error
        };
    }
}
