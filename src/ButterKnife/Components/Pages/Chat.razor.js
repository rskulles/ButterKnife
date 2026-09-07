let componentRef = null;

export function wireInput(textarea, component) {
    componentRef = component;
    if (!textarea || textarea.dataset.wired === "1") {
        return;
    }
    textarea.dataset.wired = "1";
    textarea.addEventListener("keydown", (e) => {
        if (e.key === "Enter" && !e.shiftKey && !e.isComposing) {
            e.preventDefault();
            if (!textarea.disabled) {
                component.invokeMethodAsync("SendFromKeyboardAsync");
            }
        }
    });
}

export function scrollToBottom(el) {
    if (el) {
        el.scrollTop = el.scrollHeight;
    }
}

export function focus(el) {
    el?.focus();
}

// ---- Dictation ---------------------------------------------------------------
// Server mode: MediaRecorder captures audio; .NET pulls it via takeRecording() and posts it to the
// configured transcription connection. Browser mode: the Web Speech API (Chrome/Edge; Chrome sends audio
// to Google) streams interim/final text back to .NET. Only one dictation runs at a time.

let recorder = null;
let recorderStream = null;
let recordedChunks = [];
let recording = null; // { bytes: Uint8Array, mimeType }
let recognition = null;

export async function startDictation(serverMode) {
    if (serverMode) {
        if (!navigator.mediaDevices?.getUserMedia || typeof MediaRecorder === "undefined") {
            return "error:This browser cannot record audio (needs a secure context: https or localhost).";
        }
        try {
            recorderStream = await navigator.mediaDevices.getUserMedia({ audio: true });
        } catch (e) {
            return "error:Microphone unavailable: " + (e?.message || e?.name || "permission denied");
        }
        const mimeType = ["audio/webm;codecs=opus", "audio/webm", "audio/ogg;codecs=opus", "audio/mp4"]
            .find(t => MediaRecorder.isTypeSupported(t)) || "";
        recordedChunks = [];
        recorder = new MediaRecorder(recorderStream, mimeType ? { mimeType } : undefined);
        recorder.ondataavailable = (e) => { if (e.data && e.data.size > 0) recordedChunks.push(e.data); };
        recorder.onstop = async () => {
            const type = recorder.mimeType || mimeType || "audio/webm";
            const blob = new Blob(recordedChunks, { type });
            recorderStream?.getTracks().forEach(t => t.stop());
            recorderStream = null;
            recorder = null;
            recording = { bytes: new Uint8Array(await blob.arrayBuffer()), mimeType: type };
            await componentRef?.invokeMethodAsync("OnRecordingReady", type, recording.bytes.byteLength);
        };
        recorder.start(250);
        return "server";
    }

    const Recognition = window.SpeechRecognition || window.webkitSpeechRecognition;
    if (!Recognition) {
        return "unsupported";
    }
    recognition = new Recognition();
    recognition.continuous = true;
    recognition.interimResults = true;
    recognition.lang = navigator.language || "en-US";
    let pendingError = null;
    recognition.onresult = (event) => {
        let finalText = "";
        let interim = "";
        for (let i = event.resultIndex; i < event.results.length; i++) {
            const r = event.results[i];
            if (r.isFinal) finalText += r[0].transcript;
            else interim += r[0].transcript;
        }
        componentRef?.invokeMethodAsync("OnDictation", finalText, interim);
    };
    recognition.onerror = (event) => {
        if (event.error !== "aborted" && event.error !== "no-speech") {
            pendingError = "Browser dictation error: " + event.error;
        }
    };
    recognition.onend = () => {
        recognition = null;
        componentRef?.invokeMethodAsync("OnDictationEnded", pendingError);
    };
    try {
        recognition.start();
    } catch (e) {
        recognition = null;
        return "error:Could not start browser dictation: " + (e?.message || e);
    }
    return "browser";
}

export function stopDictation() {
    if (recorder && recorder.state !== "inactive") {
        recorder.stop();
    } else if (recognition) {
        recognition.stop();
    }
}

// Returns the last recording as a stream reference for .NET (IJSStreamReference), then forgets it.
export function takeRecording() {
    const bytes = recording?.bytes ?? new Uint8Array(0);
    recording = null;
    return bytes;
}

// Test hook: inject a fake recording and run the server-mode pipeline without a microphone.
export async function debugInjectRecording(base64, mimeType) {
    const bin = atob(base64);
    const bytes = new Uint8Array(bin.length);
    for (let i = 0; i < bin.length; i++) bytes[i] = bin.charCodeAt(i);
    recording = { bytes, mimeType };
    await componentRef?.invokeMethodAsync("OnRecordingReady", mimeType, bytes.byteLength);
}
