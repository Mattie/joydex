class JoydexMicrophoneSource extends AudioWorkletProcessor {
  constructor() {
    super();
    this.chunks = [];
    this.chunkOffset = 0;
    this.queuedSamples = 0;
    this.port.onmessage = (event) => this.enqueue(event.data);
  }

  enqueue(message) {
    if (!message || message.type !== 'pcm' || !(message.buffer instanceof ArrayBuffer)) return;

    const input = new Int16Array(message.buffer);
    const sourceRate = Number(message.sampleRate) || 16000;
    const targetLength = Math.max(1, Math.round(input.length * sampleRate / sourceRate));
    const output = new Float32Array(targetLength);
    const step = sourceRate / sampleRate;
    for (let i = 0; i < targetLength; i++) {
      const position = i * step;
      const left = Math.min(input.length - 1, Math.floor(position));
      const right = Math.min(input.length - 1, left + 1);
      const fraction = position - left;
      output[i] = ((input[left] * (1 - fraction)) + (input[right] * fraction)) / 32768;
    }

    this.chunks.push(output);
    this.queuedSamples += output.length;
    const maximumQueuedSamples = sampleRate * 2;
    while (this.queuedSamples > maximumQueuedSamples && this.chunks.length > 1) {
      this.queuedSamples -= this.chunks.shift().length;
      this.chunkOffset = 0;
    }
  }

  process(_inputs, outputs) {
    const channel = outputs[0]?.[0];
    if (!channel) return true;

    channel.fill(0);
    let written = 0;
    while (written < channel.length && this.chunks.length > 0) {
      const chunk = this.chunks[0];
      const available = chunk.length - this.chunkOffset;
      const count = Math.min(channel.length - written, available);
      channel.set(chunk.subarray(this.chunkOffset, this.chunkOffset + count), written);
      written += count;
      this.chunkOffset += count;
      this.queuedSamples -= count;
      if (this.chunkOffset === chunk.length) {
        this.chunks.shift();
        this.chunkOffset = 0;
      }
    }

    return true;
  }
}

class JoydexSpeakerCapture extends AudioWorkletProcessor {
  constructor() {
    super();
    this.frame = new Int16Array(480);
    this.frameOffset = 0;
    this.downsamplePhase = 0;
    this.pendingBoundaries = [];
    this.port.onmessage = (event) => {
      if (event.data?.type === 'output-boundary') {
        const boundary = {
          type: 'output-boundary',
          kind: event.data.kind,
          boundary: event.data.boundary,
        };
        if (this.frameOffset === 0) {
          this.port.postMessage(boundary);
        } else {
          // Realtime lifecycle events and the RTP audio track are independent.
          // Preserve their best-known order without padding, resetting, or gating
          // decoded audio that may arrive on either side of this annotation.
          this.pendingBoundaries.push(boundary);
        }
      }
      if (event.data?.type === 'flush') {
        if (this.frameOffset > 0) {
          this.frame.fill(0, this.frameOffset);
          this.emitCompletedFrame();
        } else {
          this.emitPendingBoundaries();
        }
        this.downsamplePhase = 0;
        this.port.postMessage({ type: 'capture-flushed' });
      }
    };
  }

  emitPendingBoundaries() {
    while (this.pendingBoundaries.length > 0) {
      this.port.postMessage(this.pendingBoundaries.shift());
    }
  }

  emitCompletedFrame() {
    const complete = this.frame.buffer;
    this.port.postMessage(complete, [complete]);
    this.frame = new Int16Array(480);
    this.frameOffset = 0;
    this.emitPendingBoundaries();
  }

  process(inputs, outputs) {
    const input = inputs[0]?.[0];
    const output = outputs[0]?.[0];
    if (output) {
      output.fill(0);
      if (input) output.set(input.subarray(0, output.length));
    }

    if (!input) return true;

    // Codex WebRTC audio is decoded at 48 kHz. Capture the remote track
    // continuously: data-channel turn events are annotations, not a safe gate
    // for RTP audio. Keep every other sample for exact 20 ms, 24 kHz frames.
    for (let i = this.downsamplePhase; i < input.length; i += 2) {
      const sample = Math.max(-1, Math.min(1, input[i]));
      this.frame[this.frameOffset++] = sample < 0
        ? Math.round(sample * 32768)
        : Math.round(sample * 32767);
      if (this.frameOffset === this.frame.length) {
        this.emitCompletedFrame();
      }
    }
    this.downsamplePhase = (this.downsamplePhase + input.length) % 2;
    return true;
  }
}

registerProcessor('joydex-microphone-source', JoydexMicrophoneSource);
registerProcessor('joydex-speaker-capture', JoydexSpeakerCapture);
