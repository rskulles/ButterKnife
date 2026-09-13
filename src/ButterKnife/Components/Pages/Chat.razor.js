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

// ---- Images from the clipboard or drag-and-drop -------------------------------------------------
// Pasting into the composer or dropping onto the chat hands image files here. Large or unusual formats are
// re-encoded as a bounded JPEG (same rule as the file picker), the bytes wait in a queue, .NET is told, and it
// pulls them with takeDroppedImage() as a stream (the SignalR message limit is far below an image).
const droppedImages = [];
const RESIZE_ABOVE_BYTES = 1_500_000;
const MAX_DIMENSION = 1568;
const SUPPORTED = new Set(["image/jpeg", "image/png", "image/gif", "image/webp"]);

export function wireImageInput(dropZone, textarea, component) {
    if (!dropZone || dropZone.dataset.imageWired === "1") {
        return;
    }
    dropZone.dataset.imageWired = "1";

    textarea?.addEventListener("paste", (e) => {
        const files = [...(e.clipboardData?.items ?? [])]
            .filter((item) => item.kind === "file" && item.type.startsWith("image/"))
            .map((item) => item.getAsFile())
            .filter(Boolean);
        if (files.length) {
            e.preventDefault();
            ingestImages(files, component);
        }
    });

    let depth = 0;
    dropZone.addEventListener("dragenter", (e) => { if (hasFiles(e)) { e.preventDefault(); depth++; dropZone.classList.add("drop-target"); } });
    dropZone.addEventListener("dragover", (e) => { if (hasFiles(e)) { e.preventDefault(); e.dataTransfer.dropEffect = "copy"; } });
    dropZone.addEventListener("dragleave", () => { if (--depth <= 0) { depth = 0; dropZone.classList.remove("drop-target"); } });
    dropZone.addEventListener("drop", (e) => {
        if (!hasFiles(e)) {
            return;
        }
        e.preventDefault();
        depth = 0;
        dropZone.classList.remove("drop-target");
        ingestImages([...e.dataTransfer.files].filter((f) => f.type.startsWith("image/")), component);
    });
}

function hasFiles(e) {
    return [...(e.dataTransfer?.types ?? [])].includes("Files");
}

async function ingestImages(files, component) {
    for (const file of files) {
        let bytes, type = file.type;
        try {
            if (file.size > RESIZE_ABOVE_BYTES || !SUPPORTED.has(type)) {
                ({ bytes, type } = await toBoundedJpeg(file));
            } else {
                bytes = new Uint8Array(await file.arrayBuffer());
            }
        } catch {
            bytes = new Uint8Array(0);
        }
        droppedImages.push(bytes);
        await component.invokeMethodAsync("OnImagePasted", file.name || "pasted image", type, bytes.byteLength);
    }
}

async function toBoundedJpeg(file) {
    const bitmap = await createImageBitmap(file);
    const scale = Math.min(1, MAX_DIMENSION / Math.max(bitmap.width, bitmap.height));
    const canvas = document.createElement("canvas");
    canvas.width = Math.max(1, Math.round(bitmap.width * scale));
    canvas.height = Math.max(1, Math.round(bitmap.height * scale));
    canvas.getContext("2d").drawImage(bitmap, 0, 0, canvas.width, canvas.height);
    bitmap.close?.();
    const blob = await new Promise((resolve) => canvas.toBlob(resolve, "image/jpeg", 0.85));
    return { bytes: new Uint8Array(await blob.arrayBuffer()), type: "image/jpeg" };
}

export function takeDroppedImage() {
    return droppedImages.shift() ?? new Uint8Array(0);
}

// Code blocks in rendered replies: syntax highlighting (highlight.js, loaded in App.razor) and a copy button.
// Called from .NET once a transcript settles (load, reply finished); already-enhanced blocks are skipped, and a
// block Blazor re-renders comes back without the marker, so it is enhanced again.
export function enhanceCodeBlocks(root) {
    if (!root) {
        return;
    }
    for (const pre of root.querySelectorAll(".msg-markdown pre")) {
        if (pre.dataset.enhanced === "1") {
            continue;
        }
        pre.dataset.enhanced = "1";
        const code = pre.querySelector("code");
        if (code && window.hljs && !code.classList.contains("hljs")) {
            try { window.hljs.highlightElement(code); } catch { /* unknown language: leave it plain */ }
        }
        const button = document.createElement("button");
        button.type = "button";
        button.className = "code-copy btn btn-sm";
        button.title = "Copy code";
        button.setAttribute("aria-label", "Copy code");
        button.innerHTML = '<i class="bi bi-clipboard" aria-hidden="true"></i>';
        button.addEventListener("click", async () => {
            const ok = await copyText(code ? code.innerText : pre.innerText);
            button.innerHTML = ok
                ? '<i class="bi bi-clipboard-check" aria-hidden="true"></i> Copied'
                : '<i class="bi bi-clipboard-x" aria-hidden="true"></i> Failed';
            setTimeout(() => { button.innerHTML = '<i class="bi bi-clipboard" aria-hidden="true"></i>'; }, 1500);
        });
        pre.appendChild(button);
    }
}

// The async Clipboard API needs a secure context; a phone on http://192.168.x.x has none, so fall back to the
// selection-based command, which still works there.
export async function copyText(text) {
    try {
        if (navigator.clipboard && window.isSecureContext) {
            await navigator.clipboard.writeText(text);
            return true;
        }
    } catch { /* fall through */ }
    try {
        const area = document.createElement("textarea");
        area.value = text;
        area.setAttribute("readonly", "");
        area.style.position = "fixed";
        area.style.opacity = "0";
        document.body.appendChild(area);
        area.select();
        const ok = document.execCommand("copy");
        area.remove();
        return ok;
    } catch {
        return false;
    }
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

let streamFactory = null;      // test hook: replaces getUserMedia
let monitor = null;            // { ctx, timer, stop() } for silence detection + level meter
let stopReason = "manual";
const monitorState = { ticks: 0, rms: 0, threshold: null, speechHeard: false, quietMs: 0, autoStop: null, lastError: null, phase: "idle" };

// Diagnostics for tests: what the recorder and detector are doing right now.
export function debugState() {
    return Object.assign({}, monitorState, {
        recorderState: recorder?.state ?? null,
        monitorActive: !!monitor,
        hasRecording: !!recording,
        stopReason,
    });
}

const PREFS_KEY = "butterknife.dictation";

export function loadDictationPrefs() {
    try {
        const raw = localStorage.getItem(PREFS_KEY);
        return raw ? JSON.parse(raw) : null;
    } catch {
        return null;
    }
}

export function saveDictationPrefs(prefs) {
    try {
        localStorage.setItem(PREFS_KEY, JSON.stringify(prefs));
    } catch {
        // private mode etc.; defaults from the server still apply
    }
}

async function acquireStream() {
    if (streamFactory) {
        return await streamFactory();
    }
    return await navigator.mediaDevices.getUserMedia({ audio: true });
}

// Simple energy-based voice activity detection: sample the noise floor briefly, treat sustained energy
// well above it as speech, and once speech has been heard stop after `silenceMs` of quiet.
function startMonitor(stream, micButton, options, onAutoStop) {
    const AudioCtx = window.AudioContext || window.webkitAudioContext;
    const ctx = new AudioCtx();
    const source = ctx.createMediaStreamSource(stream);
    const analyser = ctx.createAnalyser();
    analyser.fftSize = 1024;
    source.connect(analyser);
    const data = new Float32Array(analyser.fftSize);

    const startedAt = performance.now();
    const floorSamples = [];
    let threshold = null;
    let speechHeard = false;
    let quietSince = null;
    Object.assign(monitorState, { ticks: 0, rms: 0, threshold: null, speechHeard: false, quietMs: 0, autoStop: options.autoStop, lastError: null, phase: "monitoring" });

    const timer = setInterval(() => {
      try {
        analyser.getFloatTimeDomainData(data);
        let sum = 0;
        for (let i = 0; i < data.length; i++) sum += data[i] * data[i];
        const rms = Math.sqrt(sum / data.length);
        const now = performance.now();
        monitorState.ticks++;
        monitorState.rms = rms;
        monitorState.threshold = threshold;
        monitorState.speechHeard = speechHeard;
        monitorState.quietMs = quietSince === null ? 0 : now - quietSince;

        if (micButton) {
            micButton.style.setProperty("--mic-level", Math.min(1, rms * 8).toFixed(2));
        }

        if (now - startedAt > options.maxSeconds * 1000) {
            onAutoStop("max");
            return;
        }
        if (!options.autoStop) {
            return;
        }

        if (threshold === null) {
            floorSamples.push(rms);
            if (now - startedAt >= 400) {
                const floor = floorSamples.reduce((a, b) => a + b, 0) / floorSamples.length;
                threshold = Math.max(floor * 3, 0.012);
            }
            return;
        }

        if (rms > threshold) {
            speechHeard = true;
            quietSince = null;
        } else if (speechHeard) {
            quietSince ??= now;
            if (now - quietSince >= options.silenceMs) {
                clearInterval(timer);
                onAutoStop("silence");
            }
        }
      } catch (e) {
        monitorState.lastError = String(e);
      }
    }, 100);

    return {
        stop() {
            clearInterval(timer);
            try { source.disconnect(); } catch { }
            ctx.close();
            if (micButton) micButton.style.removeProperty("--mic-level");
        },
    };
}

export async function startDictation(serverMode, options, micButton) {
    options = Object.assign({ autoStop: true, silenceMs: 1500, maxSeconds: 120 }, options || {});
    if (serverMode) {
        if ((!navigator.mediaDevices?.getUserMedia && !streamFactory) || typeof MediaRecorder === "undefined") {
            return "error:This browser cannot record audio (needs a secure context: https or localhost).";
        }
        try {
            recorderStream = await acquireStream();
        } catch (e) {
            return "error:Microphone unavailable: " + (e?.message || e?.name || "permission denied");
        }
        const mimeType = ["audio/webm;codecs=opus", "audio/webm", "audio/ogg;codecs=opus", "audio/mp4"]
            .find(t => MediaRecorder.isTypeSupported(t)) || "";
        recordedChunks = [];
        stopReason = "manual";
        recorder = new MediaRecorder(recorderStream, mimeType ? { mimeType } : undefined);
        recorder.ondataavailable = (e) => { if (e.data && e.data.size > 0) recordedChunks.push(e.data); };
        recorder.onstop = async () => {
            monitorState.phase = "stopped";
            monitor?.stop();
            monitor = null;
            const type = recorder.mimeType || mimeType || "audio/webm";
            const blob = new Blob(recordedChunks, { type });
            recorderStream?.getTracks().forEach(t => t.stop());
            recorderStream = null;
            recorder = null;
            // Whisper servers universally accept 16 kHz mono PCM WAV (stock whisper.cpp accepts nothing else),
            // so convert in the browser; fall back to the raw container if decoding fails.
            monitorState.phase = "converting";
            try {
                recording = { bytes: await toWav16k(blob), mimeType: "audio/wav" };
            } catch (e) {
                console.warn("WAV conversion failed, sending raw recording", e);
                recording = { bytes: new Uint8Array(await blob.arrayBuffer()), mimeType: type };
            }
            monitorState.phase = "delivering";
            await componentRef?.invokeMethodAsync("OnRecordingReady", recording.mimeType, recording.bytes.byteLength, stopReason);
            monitorState.phase = "delivered";
        };
        recorder.start(250);
        try {
            monitor = startMonitor(recorderStream, micButton, options, (reason) => {
                stopReason = reason;
                stopDictation();
            });
        } catch (e) {
            console.warn("Audio monitoring unavailable; manual stop only", e);
        }
        return "server";
    }

    const Recognition = window.SpeechRecognition || window.webkitSpeechRecognition;
    if (!Recognition) {
        return "unsupported";
    }
    recognition = new Recognition();
    recognition.continuous = !options.autoStop; // with auto-stop the API ends on its own after a pause
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

// Test hook: record from a synthetic microphone that plays a tone for `toneSeconds` and then goes silent,
// so the silence detector can be exercised without a real device.
let syntheticCtx = null; // pinned so the synthetic source is not garbage-collected mid-recording

export function debugUseSyntheticMicrophone(toneSeconds) {
    streamFactory = async () => {
        const AudioCtx = window.AudioContext || window.webkitAudioContext;
        syntheticCtx?.close();
        const ctx = syntheticCtx = new AudioCtx();
        const destination = ctx.createMediaStreamDestination();
        const osc = ctx.createOscillator();
        const gain = ctx.createGain();
        gain.gain.value = 0.3;
        osc.frequency.value = 220;
        osc.connect(gain).connect(destination);
        osc.start(ctx.currentTime + 0.6);           // quiet first, so the noise floor is sampled on silence
        osc.stop(ctx.currentTime + 0.6 + toneSeconds);
        await ctx.resume();
        return destination.stream;
    };
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
