let meeting = null;

const DEFAULT_AUDIO_CONSTRAINTS = {
    echoCancellation: true,
    noiseSuppression: true,
    autoGainControl: true
};

const DEFAULT_SCREENSHARE_VIDEO_CONSTRAINTS = {
    width: { ideal: 1920, max: 3840 },
    height: { ideal: 1080, max: 2160 },
    frameRate: { ideal: 30, max: 30 }
};

const SDK_POLL_INTERVAL_MS = 50;
const SDK_SCRIPT_PATH = "_content/Valour.Client/js/realtimekit.js";
let sdkScriptLoadPromise = null;
let sdkScriptLoadError = null;

function getGlobalScope() {
    if (typeof window !== 'undefined') {
        return window;
    }

    if (typeof globalThis !== 'undefined') {
        return globalThis;
    }

    return null;
}

function getRealtimeKitClient() {
    const scope = getGlobalScope();
    const sdk = scope?.RealtimeKitClient;

    if (!sdk) {
        throw new Error(`RealtimeKit SDK was not found. Ensure '${SDK_SCRIPT_PATH}' is loaded before initializing.`);
    }

    return sdk;
}

function resolveSdkScriptUrl() {
    try {
        if (typeof document !== "undefined" && typeof document.baseURI === "string") {
            return new URL(SDK_SCRIPT_PATH, document.baseURI).toString();
        }

        if (typeof location !== "undefined" && typeof location.href === "string") {
            return new URL(SDK_SCRIPT_PATH, location.href).toString();
        }
    } catch {
        // Ignore invalid base URI and fallback to the raw path.
    }

    return SDK_SCRIPT_PATH;
}

function isSdkScriptSource(source) {
    if (typeof source !== "string" || source.length === 0) {
        return false;
    }

    return source.includes(SDK_SCRIPT_PATH) || source.endsWith("/realtimekit.js");
}

function findSdkScriptTag() {
    if (typeof document === "undefined") {
        return null;
    }

    const scripts = document.querySelectorAll("script[src]");
    for (const script of scripts) {
        if (isSdkScriptSource(script.getAttribute("src")) || isSdkScriptSource(script.src)) {
            return script;
        }
    }

    return null;
}

function startSdkScriptLoad() {
    if (getGlobalScope()?.RealtimeKitClient || typeof document === "undefined" || sdkScriptLoadPromise) {
        return;
    }

    const existingScript = findSdkScriptTag();
    const script = existingScript ?? document.createElement("script");

    if (!existingScript) {
        script.src = resolveSdkScriptUrl();
        script.async = true;
    }

    sdkScriptLoadPromise = new Promise((resolve, reject) => {
        if (getGlobalScope()?.RealtimeKitClient) {
            resolve();
            return;
        }

        const cleanup = () => {
            script.removeEventListener("load", onLoad);
            script.removeEventListener("error", onError);
        };

        const onLoad = () => {
            cleanup();
            resolve();
        };

        const onError = () => {
            cleanup();
            reject(new Error(`Failed to load RealtimeKit SDK script from '${script.src || resolveSdkScriptUrl()}'.`));
        };

        script.addEventListener("load", onLoad);
        script.addEventListener("error", onError);

        if (!existingScript) {
            const parent = document.head ?? document.body ?? document.documentElement;
            if (!parent) {
                cleanup();
                reject(new Error("Cannot load RealtimeKit SDK because the document does not have a script host element."));
                return;
            }

            parent.appendChild(script);
        }
    }).catch((error) => {
        sdkScriptLoadError = error;
        throw error;
    });
}

async function waitForSdk(timeoutMs = 20000) {
    const startedAt = Date.now();
    startSdkScriptLoad();

    while (Date.now() - startedAt < timeoutMs) {
        const scope = getGlobalScope();
        if (scope?.RealtimeKitClient) {
            return scope.RealtimeKitClient;
        }

        if (sdkScriptLoadError) {
            throw sdkScriptLoadError;
        }

        await new Promise((resolve) => setTimeout(resolve, SDK_POLL_INTERVAL_MS));
    }

    const script = findSdkScriptTag();
    throw new Error(
        `Timed out waiting for RealtimeKit SDK after ${timeoutMs}ms. Expected script '${script?.src || resolveSdkScriptUrl()}'.`
    );
}

async function withTimeout(promise, timeoutMs, timeoutMessage) {
    if (timeoutMs <= 0) {
        return await promise;
    }

    let timeoutHandle = null;

    try {
        return await Promise.race([
            promise,
            new Promise((_, reject) => {
                timeoutHandle = setTimeout(
                    () => reject(new Error(timeoutMessage)),
                    timeoutMs
                );
            })
        ]);
    } finally {
        if (timeoutHandle !== null) {
            clearTimeout(timeoutHandle);
        }
    }
}

function canUseMediaDevices() {
    return typeof navigator !== "undefined"
        && navigator.mediaDevices !== undefined
        && navigator.mediaDevices !== null;
}

function canRequestMicrophoneAccess() {
    return canUseMediaDevices()
        && typeof navigator.mediaDevices.getUserMedia === "function";
}

function canRequestCameraAccess() {
    return canRequestMicrophoneAccess();
}

function canRequestScreenShareAccess() {
    return canUseMediaDevices()
        && typeof navigator.mediaDevices.getDisplayMedia === "function";
}

function isAndroidDeviceInternal() {
    if (typeof navigator === "undefined") {
        return false;
    }

    const userAgent = navigator.userAgent ?? "";
    return /android/i.test(userAgent);
}

function normalizePermissionState(state) {
    if (state === "granted" || state === "denied" || state === "prompt") {
        return state;
    }

    return "unknown";
}

function stopStreamTracks(stream) {
    if (!stream?.getTracks) {
        return;
    }

    for (const track of stream.getTracks()) {
        track.stop();
    }
}

function stopMediaTrack(track) {
    if (!track || typeof track.stop !== "function") {
        return;
    }

    try {
        track.stop();
    } catch {
        // Ignore track stop failures during teardown.
    }
}

function collectLocalTracks(activeMeeting) {
    const self = activeMeeting?.self;
    if (!self) {
        return [];
    }

    const tracks = [
        self.audioTrack,
        self.rawAudioTrack,
        self.videoTrack,
        self.rawVideoTrack,
        self.screenShareTrack,
        self.screenShareAudioTrack,
        self.screenShareTracks?.audio,
        self.screenShareTracks?.video,
        self.screenshareTrack,
        self.screenshareAudioTrack,
        self.screenshareTracks?.audio,
        self.screenshareTracks?.video
    ].filter(Boolean);

    const uniqueTracks = [];
    const seenTrackIds = new Set();

    for (const track of tracks) {
        const trackId = typeof track.id === "string" && track.id.length > 0 ? track.id : null;
        if (trackId && seenTrackIds.has(trackId)) {
            continue;
        }

        if (trackId) {
            seenTrackIds.add(trackId);
        }

        uniqueTracks.push(track);
    }

    return uniqueTracks;
}

async function teardownMeeting(activeMeeting, endCall = false) {
    if (!activeMeeting) {
        return;
    }

    const self = activeMeeting?.self;
    const localTracks = collectLocalTracks(activeMeeting);

    // Stop local capture immediately so the mic/camera can't remain hot if SDK leave hangs.
    for (const track of localTracks) {
        stopMediaTrack(track);
    }

    try {
        if (self?.screenShareEnabled && typeof self.disableScreenShare === "function") {
            await withTimeout(
                self.disableScreenShare(),
                1000,
                "Timed out disabling screenshare during teardown."
            );
        }
    } catch {
        // Ignore best-effort teardown failures.
    }

    try {
        if (self?.videoEnabled && typeof self.disableVideo === "function") {
            await withTimeout(
                self.disableVideo(),
                1000,
                "Timed out disabling video during teardown."
            );
        }
    } catch {
        // Ignore best-effort teardown failures.
    }

    try {
        if (self?.audioEnabled && typeof self.disableAudio === "function") {
            await withTimeout(
                self.disableAudio(),
                1000,
                "Timed out disabling audio during teardown."
            );
        }
    } catch {
        // Ignore best-effort teardown failures.
    }

    try {
        if (typeof activeMeeting.leaveRoom === "function") {
            await withTimeout(
                activeMeeting.leaveRoom(endCall),
                2000,
                "Timed out leaving room during teardown."
            );
        }
    } catch {
        // Ignore best-effort teardown failures.
    }
}

async function requestMicrophonePermissionInternal() {
    return await requestMediaPermissionInternal(
        canRequestMicrophoneAccess(),
        { audio: true }
    );
}

async function requestCameraPermissionInternal() {
    return await requestMediaPermissionInternal(
        canRequestCameraAccess(),
        { video: true }
    );
}

async function requestPlatformVideoPermissionInternal() {
    if (isAndroidDeviceInternal()) {
        return await requestMediaPermissionInternal(
            canRequestCameraAccess(),
            { audio: true, video: true }
        );
    }

    return await requestCameraPermissionInternal();
}

async function requestMediaPermissionInternal(canRequestAccess, constraints) {
    if (!canRequestAccess) {
        return false;
    }

    let stream = null;

    try {
        stream = await navigator.mediaDevices.getUserMedia(constraints);
        return true;
    } catch (error) {
        try {
            const reason = error?.name ? `${error.name}: ${error.message ?? ""}` : String(error);
            console.warn("Failed to request media permission.", { constraints, reason });
        } catch {
            // Ignore console failures.
        }

        return false;
    } finally {
        stopStreamTracks(stream);
    }
}

function getMeetingOrThrow() {
    if (!meeting) {
        throw new Error("RealtimeKit meeting is not initialized. Call init() first.");
    }

    return meeting;
}

async function applyDefaultAudioConstraints(activeMeeting) {
    try {
        const track = activeMeeting.self?.audioTrack;
        if (track && typeof track.applyConstraints === "function") {
            await track.applyConstraints(DEFAULT_AUDIO_CONSTRAINTS);
        }
    } catch {
        // Browser does not support applying these constraints — silently continue.
    }
}

async function applyDefaultScreenShareConstraints(activeMeeting) {
    const self = activeMeeting?.self;
    if (!self) {
        return;
    }

    try {
        if (typeof self.updateScreenshareConstraints === "function") {
            await self.updateScreenshareConstraints(DEFAULT_SCREENSHARE_VIDEO_CONSTRAINTS);
        }
    } catch {
        // SDK may reject manual screenshare updates on some environments.
    }

    try {
        const screenShareTrack = self.screenShareTracks?.video ?? self.screenShareTrack ?? null;
        if (screenShareTrack && typeof screenShareTrack.applyConstraints === "function") {
            await screenShareTrack.applyConstraints(DEFAULT_SCREENSHARE_VIDEO_CONSTRAINTS);
        }
    } catch {
        // Browser does not support applying these constraints — silently continue.
    }
}

function resolveTargetPath(root, path) {
    const segments = path.split('.');
    let current = root;

    for (let i = 0; i < segments.length - 1; i++) {
        current = current?.[segments[i]];
        if (!current) {
            throw new Error(`Path '${path}' is invalid. Missing segment '${segments[i]}'.`);
        }
    }

    const methodName = segments[segments.length - 1];
    const method = current?.[methodName];

    if (typeof method !== 'function') {
        throw new Error(`Path '${path}' does not resolve to a function.`);
    }

    return { target: current, method };
}

export function isInitialized() {
    return meeting !== null;
}

export async function init(options, sdkLoadTimeoutMs = 20000, initTimeoutMs = 15000) {
    const sdk = await waitForSdk(sdkLoadTimeoutMs);
    meeting = await withTimeout(
        sdk.init(options),
        initTimeoutMs,
        `Timed out initializing RealtimeKit after ${initTimeoutMs}ms.`
    );
}

export async function joinRoom(timeoutMs = 25000) {
    const activeMeeting = getMeetingOrThrow();
    await withTimeout(
        activeMeeting.joinRoom(),
        timeoutMs,
        `Timed out joining voice room after ${timeoutMs}ms.`
    );
}

export async function leaveRoom(endCall = false) {
    if (!meeting) {
        return;
    }

    const activeMeeting = meeting;
    // Clear the shared reference first so repeated teardown calls don't race on the same instance.
    meeting = null;

    try {
        await teardownMeeting(activeMeeting, endCall);
    } catch {
        // Teardown is best-effort; local tracks are already stopped above.
    }
}

export async function enableAudio() {
    const activeMeeting = getMeetingOrThrow();
    await activeMeeting.self.enableAudio();
    await applyDefaultAudioConstraints(activeMeeting);
}

export async function disableAudio() {
    const activeMeeting = getMeetingOrThrow();
    await activeMeeting.self.disableAudio();
}

export async function enableVideo() {
    const activeMeeting = getMeetingOrThrow();
    await activeMeeting.self.enableVideo();
}

export async function disableVideo() {
    const activeMeeting = getMeetingOrThrow();
    await activeMeeting.self.disableVideo();
}

export async function enableScreenShare() {
    const activeMeeting = getMeetingOrThrow();
    await applyDefaultScreenShareConstraints(activeMeeting);
    await activeMeeting.self.enableScreenShare();
    await applyDefaultScreenShareConstraints(activeMeeting);
}

export async function disableScreenShare() {
    const activeMeeting = getMeetingOrThrow();
    await activeMeeting.self.disableScreenShare();
}

export async function setDevice(device) {
    const activeMeeting = getMeetingOrThrow();
    await activeMeeting.self.setDevice(device);

    if (device?.kind === "audioinput") {
        await applyDefaultAudioConstraints(activeMeeting);
    }
}

export async function getAllDevices() {
    const activeMeeting = getMeetingOrThrow();
    return await activeMeeting.self.getAllDevices();
}

export async function getAudioInputDevices() {
    if (!canUseMediaDevices() || typeof navigator.mediaDevices.enumerateDevices !== "function") {
        return [];
    }

    let unnamedMicIndex = 1;
    const devices = await navigator.mediaDevices.enumerateDevices();

    return devices
        .filter((device) => device.kind === "audioinput")
        .map((device) => {
            const hasLabel = typeof device.label === "string" && device.label.trim().length > 0;
            const label = hasLabel ? device.label : `Microphone ${unnamedMicIndex++}`;

            return {
                deviceId: device.deviceId,
                label
            };
        });
}

export async function getVideoInputDevices() {
    if (!canUseMediaDevices() || typeof navigator.mediaDevices.enumerateDevices !== "function") {
        return [];
    }

    let unnamedCameraIndex = 1;
    const devices = await navigator.mediaDevices.enumerateDevices();

    return devices
        .filter((device) => device.kind === "videoinput")
        .map((device) => {
            const hasLabel = typeof device.label === "string" && device.label.trim().length > 0;
            const label = hasLabel ? device.label : `Camera ${unnamedCameraIndex++}`;

            return {
                deviceId: device.deviceId,
                label
            };
        });
}

export async function getMicrophonePermissionState() {
    if (!canRequestMicrophoneAccess()) {
        return "unsupported";
    }

    return await getPermissionStateAsync("microphone");
}

export async function getCameraPermissionState() {
    if (!canRequestCameraAccess()) {
        return "unsupported";
    }

    return await getPermissionStateAsync("camera");
}

async function getPermissionStateAsync(name) {
    if (!navigator.permissions || typeof navigator.permissions.query !== "function") {
        return "unknown";
    }

    try {
        const permission = await navigator.permissions.query({ name });
        return normalizePermissionState(permission?.state);
    } catch {
        return "unknown";
    }
}

export async function requestMicrophonePermission() {
    return await requestMicrophonePermissionInternal();
}

export async function requestCameraPermission() {
    return await requestCameraPermissionInternal();
}

export async function requestPlatformVideoPermission() {
    return await requestPlatformVideoPermissionInternal();
}

export function isScreenShareSupported() {
    return canRequestScreenShareAccess();
}

export function isAndroidDevice() {
    return isAndroidDeviceInternal();
}

export function getSelfState() {
    const activeMeeting = getMeetingOrThrow();
    const self = activeMeeting.self;

    return {
        id: self?.id ?? null,
        name: self?.name ?? null,
        picture: self?.picture ?? null,
        audioEnabled: self?.audioEnabled ?? false,
        videoEnabled: self?.videoEnabled ?? false,
        screenShareEnabled: self?.screenShareEnabled ?? false
    };
}

function getJoinedParticipants(activeMeeting) {
    return activeMeeting?.participants?.joined?.toArray?.() ?? [];
}

function getParticipantById(activeMeeting, participantId) {
    if (!participantId) {
        return null;
    }

    if (activeMeeting?.self?.id === participantId) {
        return activeMeeting.self;
    }

    const joined = getJoinedParticipants(activeMeeting);
    return joined.find((participant) => participant?.id === participantId) ?? null;
}

function parseUserIdString(participant) {
    const customParticipantId = participant?.customParticipantId ?? participant?.clientSpecificId ?? null;
    if (typeof customParticipantId === "string" && customParticipantId.length > 0) {
        const [candidate] = customParticipantId.split(":", 1);
        if (/^[0-9]+$/.test(candidate)) {
            return candidate;
        }
    }

    const rawUserId = participant?.userId;
    if (typeof rawUserId === "string" && /^[0-9]+$/.test(rawUserId)) {
        return rawUserId;
    }

    if (typeof rawUserId === "number" && Number.isFinite(rawUserId)) {
        return String(Math.trunc(rawUserId));
    }

    return null;
}

function getParticipantVideoTrack(participant) {
    return participant?.videoTrack
        ?? participant?.videoTracks?.video
        ?? participant?.videoTracks?.camera
        ?? null;
}

function getParticipantScreenShareTrack(participant) {
    return participant?.screenShareTrack
        ?? participant?.screenShareTracks?.video
        ?? participant?.screenshareTrack
        ?? participant?.screenshareTracks?.video
        ?? null;
}

function getParticipantScreenShareAudioTrack(participant) {
    return participant?.screenShareAudioTrack
        ?? participant?.screenShareTracks?.audio
        ?? participant?.screenshareAudioTrack
        ?? participant?.screenshareTracks?.audio
        ?? null;
}

function mapParticipantSnapshot(participant, isSelf = false) {
    if (!participant?.id) {
        return null;
    }

    const videoTrack = getParticipantVideoTrack(participant);
    const screenShareTrack = getParticipantScreenShareTrack(participant);
    const screenShareAudioTrack = getParticipantScreenShareAudioTrack(participant);

    return {
        peerId: participant.id,
        userId: parseUserIdString(participant),
        customParticipantId: participant.customParticipantId ?? participant.clientSpecificId ?? null,
        name: participant.name ?? participant.displayName ?? null,
        picture: participant.picture ?? null,
        audioEnabled: participant.audioEnabled ?? false,
        videoEnabled: participant.videoEnabled ?? false,
        screenShareEnabled: participant.screenShareEnabled ?? false,
        hasAudioTrack: !!participant.audioTrack,
        audioTrackId: participant.audioTrack?.id ?? null,
        hasVideoTrack: !!videoTrack,
        videoTrackId: videoTrack?.id ?? null,
        hasScreenShareTrack: !!screenShareTrack,
        screenShareTrackId: screenShareTrack?.id ?? null,
        hasScreenShareAudioTrack: !!screenShareAudioTrack,
        screenShareAudioTrackId: screenShareAudioTrack?.id ?? null,
        isSelf
    };
}

function getAudioElement(elementId) {
    if (typeof document === "undefined") {
        return null;
    }

    const element = document.getElementById(elementId);
    return element instanceof HTMLAudioElement ? element : null;
}

function getVideoElement(elementId) {
    if (typeof document === "undefined") {
        return null;
    }

    const element = document.getElementById(elementId);
    return element instanceof HTMLVideoElement ? element : null;
}

function getElementAudioTrackIds(audioElement) {
    const stream = audioElement?.srcObject;
    if (!(stream instanceof MediaStream)) {
        return [];
    }

    return stream.getAudioTracks().map((track) => track.id).sort();
}

function clearAudioElement(audioElement) {
    if (!(audioElement?.srcObject instanceof MediaStream)) {
        audioElement.srcObject = null;
        return;
    }

    const currentStream = audioElement.srcObject;
    for (const track of currentStream.getTracks()) {
        currentStream.removeTrack(track);
    }

    audioElement.srcObject = null;
}

export function getParticipantsSnapshot() {
    const activeMeeting = getMeetingOrThrow();
    const participantMap = new Map();

    const joinedParticipants = getJoinedParticipants(activeMeeting);
    for (const participant of joinedParticipants) {
        const mappedParticipant = mapParticipantSnapshot(participant, false);
        if (mappedParticipant?.peerId) {
            participantMap.set(mappedParticipant.peerId, mappedParticipant);
        }
    }

    const selfParticipant = mapParticipantSnapshot(activeMeeting.self, true);
    if (selfParticipant?.peerId) {
        participantMap.set(selfParticipant.peerId, selfParticipant);
    }

    return {
        activeSpeakerPeerId: activeMeeting?.participants?.lastActiveSpeaker ?? null,
        participants: Array.from(participantMap.values())
    };
}

export function syncParticipantAudio(elementId, participantId, volume = 1.0) {
    const activeMeeting = getMeetingOrThrow();
    const audioElement = getAudioElement(elementId);
    if (!audioElement) {
        return;
    }

    const participant = getParticipantById(activeMeeting, participantId);
    const micAudioTrack = participant?.audioTrack ?? null;
    const screenShareAudioTrack = getParticipantScreenShareAudioTrack(participant);
    const isSelfParticipant = participant?.id === activeMeeting?.self?.id;
    const shouldPlayMicAudio = !!participant?.audioEnabled && !!micAudioTrack;
    const shouldPlayScreenShareAudio = !!participant?.screenShareEnabled && !!screenShareAudioTrack;
    const shouldPlayAudio = !isSelfParticipant && (shouldPlayMicAudio || shouldPlayScreenShareAudio);

    audioElement.autoplay = true;
    audioElement.playsInline = true;

    if (!shouldPlayAudio) {
        clearAudioElement(audioElement);
        return;
    }

    const desiredTracks = [];
    if (shouldPlayMicAudio) {
        desiredTracks.push(micAudioTrack);
    }

    if (shouldPlayScreenShareAudio) {
        desiredTracks.push(screenShareAudioTrack);
    }

    const desiredTrackIds = desiredTracks.map((track) => track.id).sort();
    const existingTrackIds = getElementAudioTrackIds(audioElement);
    const hasSameAudioTracks = desiredTrackIds.length === existingTrackIds.length
        && desiredTrackIds.every((trackId, index) => trackId === existingTrackIds[index]);

    if (!hasSameAudioTracks) {
        audioElement.srcObject = new MediaStream(desiredTracks);
    }

    audioElement.volume = Math.max(0, Math.min(1, volume));

    const playResult = audioElement.play();
    if (playResult && typeof playResult.catch === "function") {
        playResult.catch(() => {
            // Autoplay restrictions are expected until a user interaction occurs.
        });
    }
}

function getElementVideoTrack(videoElement) {
    const stream = videoElement?.srcObject;
    if (!(stream instanceof MediaStream)) {
        return null;
    }

    const tracks = stream.getVideoTracks();
    return tracks.length > 0 ? tracks[0] : null;
}

function clearVideoElement(videoElement) {
    if (!(videoElement?.srcObject instanceof MediaStream)) {
        videoElement.srcObject = null;
        return;
    }

    const currentStream = videoElement.srcObject;
    for (const track of currentStream.getTracks()) {
        currentStream.removeTrack(track);
    }

    videoElement.srcObject = null;
}

export function syncParticipantVideo(elementId, participantId, preferScreenShare = true) {
    const activeMeeting = getMeetingOrThrow();
    const videoElement = getVideoElement(elementId);
    if (!videoElement) {
        return;
    }

    const participant = getParticipantById(activeMeeting, participantId);
    const videoTrack = getParticipantVideoTrack(participant);
    const screenShareTrack = getParticipantScreenShareTrack(participant);
    const selectedTrack = preferScreenShare ? screenShareTrack : videoTrack;
    const shouldRenderTrack = preferScreenShare
        ? !!screenShareTrack
        : !!videoTrack && !!participant?.videoEnabled;

    videoElement.autoplay = true;
    videoElement.playsInline = true;
    videoElement.muted = true;

    if (!shouldRenderTrack) {
        clearVideoElement(videoElement);
        return;
    }

    const existingTrack = getElementVideoTrack(videoElement);
    if (!existingTrack || existingTrack.id !== selectedTrack.id) {
        videoElement.srcObject = new MediaStream([selectedTrack]);
    }

    const playResult = videoElement.play();
    if (playResult && typeof playResult.catch === "function") {
        playResult.catch(() => {
            // Autoplay restrictions are expected until a user interaction occurs.
        });
    }
}

export async function invoke(path, args) {
    const activeMeeting = getMeetingOrThrow();
    const { target, method } = resolveTargetPath(activeMeeting, path);
    const normalizedArgs = Array.isArray(args) ? args : [];

    return await method.apply(target, normalizedArgs);
}

export function reset() {
    if (!meeting) {
        return;
    }

    const activeMeeting = meeting;
    meeting = null;

    // Fire-and-forget cleanup for disposal/unload paths where JS interop may be timing out.
    void teardownMeeting(activeMeeting, false);
}

export function sdkLoaded() {
    try {
        return !!getRealtimeKitClient();
    } catch {
        return false;
    }
}
