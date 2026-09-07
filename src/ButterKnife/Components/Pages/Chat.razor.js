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
            // Whisper servers universally accept 16 kHz mono PCM WAV (stock whisper.cpp accepts nothing else),
            // so convert in the browser; fall back to the raw container if decoding fails.
            try {
                recording = { bytes: await toWav16k(blob), mimeType: "audio/wav" };
            } catch (e) {
                console.warn("WAV conversion failed, sending raw recording", e);
                recording = { bytes: new Uint8Array(await blob.arrayBuffer()), mimeType: type };
            }
            await componentRef?.invokeMethodAsync("OnRecordingReady", recording.mimeType, recording.bytes.byteLength);
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

// ---- WAV conversion ----------------------------------------------------------

const WAV_RATE = 16000;

async function toWav16k(blob) {
    const AudioCtx = window.AudioContext || window.webkitAudioContext;
    const ctx = new AudioCtx();
    try {
        const decoded = await ctx.decodeAudioData(await blob.arrayBuffer());
        const length = Math.max(1, Math.ceil(decoded.duration * WAV_RATE));
        const offline = new OfflineAudioContext(1, length, WAV_RATE); // mono destination downmixes; rate change resamples
        const source = offline.createBufferSource();
        source.buffer = decoded;
        source.connect(offline.destination);
        source.start(0);
        const rendered = await offline.startRendering();
        return encodeWav(rendered.getChannelData(0), WAV_RATE);
    } finally {
        ctx.close();
    }
}

function encodeWav(samples, sampleRate) {
    const bytesPerSample = 2;
    const buffer = new ArrayBuffer(44 + samples.length * bytesPerSample);
    const view = new DataView(buffer);
    const writeString = (offset, str) => { for (let i = 0; i < str.length; i++) view.setUint8(offset + i, str.charCodeAt(i)); };
    writeString(0, "RIFF");
    view.setUint32(4, 36 + samples.length * bytesPerSample, true);
    writeString(8, "WAVE");
    writeString(12, "fmt ");
    view.setUint32(16, 16, true);          // PCM chunk size
    view.setUint16(20, 1, true);           // PCM format
    view.setUint16(22, 1, true);           // mono
    view.setUint32(24, sampleRate, true);
    view.setUint32(28, sampleRate * bytesPerSample, true);
    view.setUint16(32, bytesPerSample, true);
    view.setUint16(34, 16, true);          // bits per sample
    writeString(36, "data");
    view.setUint32(40, samples.length * bytesPerSample, true);
    let offset = 44;
    for (let i = 0; i < samples.length; i++, offset += bytesPerSample) {
        const s = Math.max(-1, Math.min(1, samples[i]));
        view.setInt16(offset, s < 0 ? s * 0x8000 : s * 0x7FFF, true);
    }
    return new Uint8Array(buffer);
}

// Test hook: synthesise a 48 kHz stereo tone, run it through the conversion, and report the WAV header.
export async function debugConvertTone(seconds) {
    const rate = 48000;
    const offline = new OfflineAudioContext(2, Math.ceil(seconds * rate), rate);
    const osc = offline.createOscillator();
    osc.frequency.value = 440;
    osc.connect(offline.destination);
    osc.start(0);
    const rendered = await offline.startRendering();
    const stereoWav = encodeWav(rendered.getChannelData(0), rate); // mono source is fine for the test; the container is what matters
    const converted = await toWav16k(new Blob([stereoWav], { type: "audio/wav" }));
    const view = new DataView(converted.buffer);
    return {
        riff: String.fromCharCode(view.getUint8(0), view.getUint8(1), view.getUint8(2), view.getUint8(3)),
        channels: view.getUint16(22, true),
        sampleRate: view.getUint32(24, true),
        bits: view.getUint16(34, true),
        bytes: converted.byteLength,
        expectedBytes: 44 + Math.ceil(seconds * WAV_RATE) * 2,
    };
}
