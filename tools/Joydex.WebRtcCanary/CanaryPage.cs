namespace Joydex.WebRtcCanary;

internal static class CanaryPage
{
    public const string Html = """
        <!doctype html>
        <html lang="en">
        <head>
          <meta charset="utf-8">
          <meta name="viewport" content="width=device-width, initial-scale=1">
          <title>Joydex WebRTC Canary</title>
          <style>
            :root { color-scheme: dark; font-family: ui-monospace, SFMono-Regular, Consolas, monospace; }
            body { margin: 0; min-height: 100vh; display: grid; place-items: center; background: #0b0d10; color: #e7edf4; }
            main { width: min(760px, calc(100vw - 40px)); border: 1px solid #303946; border-radius: 14px; padding: 24px; background: #11161c; box-shadow: 0 20px 80px #0008; }
            h1 { margin: 0 0 8px; font: 600 22px system-ui, sans-serif; }
            .sub { color: #9ca8b8; margin-bottom: 24px; line-height: 1.45; }
            #status { padding: 14px 16px; border-radius: 9px; background: #171e27; color: #8ed5ff; min-height: 42px; line-height: 1.4; }
            ol { list-style: none; padding: 0; display: grid; gap: 9px; }
            li { display: flex; align-items: center; gap: 10px; color: #778494; }
            li::before { content: '○'; width: 18px; }
            li.done { color: #b8f5d0; }
            li.done::before { content: '●'; color: #49d17d; }
            .controls { display: flex; flex-wrap: wrap; gap: 10px; margin-top: 20px; }
            button { border: 0; border-radius: 8px; padding: 11px 16px; font: 600 14px system-ui, sans-serif; cursor: pointer; background: #2f8edb; color: white; }
            button.secondary { background: #394554; }
            button.danger { background: #a9434e; }
            button:disabled { opacity: .45; cursor: default; }
            #detail { margin-top: 18px; color: #7f8a98; font-size: 12px; overflow-wrap: anywhere; }
            #metrics { margin: 12px 0 0; color: #a6b4c5; font-size: 12px; white-space: pre-wrap; }
          </style>
        </head>
        <body>
          <main>
            <h1>Joydex WebRTC Canary</h1>
            <div class="sub">Host-only experiment. The Voice PE remains on its current firmware. Start uses a muted synthetic track; a configured WAVE fixture can then exercise microphone uplink without requesting live-microphone permission.</div>
            <div id="status">Waiting for host state…</div>
            <ol>
              <li id="attestation">First-party attestation requested (observation mode only)</li>
              <li id="task">Dedicated Voice Task resolved</li>
              <li id="started">Realtime session accepted</li>
              <li id="sdp">SDP answer returned</li>
              <li id="media">WebRTC media connected</li>
              <li id="fixture">Deterministic microphone fixture sent</li>
              <li id="remoteTrack">Remote model-audio track received</li>
              <li id="audio">Model audio reached the speaker path</li>
              <li id="capture">Remote model-audio capture written</li>
              <li id="closed">Typed session-closed event received</li>
            </ol>
            <div class="controls">
              <button id="start">Start canary</button>
              <button id="fixtureInput" class="secondary" disabled>Play microphone fixture</button>
              <button id="saveCapture" class="secondary" disabled>Save remote capture</button>
              <button id="microphone" class="secondary" disabled>Use live microphone</button>
              <button id="stop" class="danger" disabled>Stop session</button>
              <button id="refresh" class="secondary">Refresh state</button>
            </div>
            <div id="detail"></div>
            <pre id="metrics"></pre>
          </main>
          <script>
            const statusEl = document.getElementById('status');
            const detailEl = document.getElementById('detail');
            const metricsEl = document.getElementById('metrics');
            const startButton = document.getElementById('start');
            const fixtureButton = document.getElementById('fixtureInput');
            const captureButton = document.getElementById('saveCapture');
            const microphoneButton = document.getElementById('microphone');
            const stopButton = document.getElementById('stop');
            let pc;
            let localStream;
            let audioContext;
            let syntheticOscillator;
            let syntheticDestination;
            let fixtureSource;
            let remoteRecorder;
            let remoteAudioElement;
            let remoteChunks = [];
            let captureCompletion;
            let captureAvailable = false;
            let audioReported = false;
            let startingPeer = false;

            async function updateReceiverStats() {
              if (!pc) { metricsEl.textContent = ''; return; }
              try {
                const reports = await pc.getStats();
                const inboundReports = [...reports.values()].filter((report) =>
                  report.type === 'inbound-rtp' && (report.kind === 'audio' || report.mediaType === 'audio'));
                const track = pc.getReceivers().find((receiver) => receiver.track?.kind === 'audio')?.track;
                metricsEl.textContent = [
                  `peer=${pc.connectionState} audioContext=${audioContext?.state ?? 'none'} recorder=${remoteRecorder?.state ?? 'none'} audioElement=${remoteAudioElement?.readyState ?? 'none'}/${remoteAudioElement?.paused ?? 'n/a'}`,
                  `remoteTrack=${track?.readyState ?? 'none'} muted=${track?.muted ?? 'n/a'} enabled=${track?.enabled ?? 'n/a'}`,
                  ...inboundReports.map((inbound) => {
                    const codec = reports.get(inbound.codecId);
                    return `codec=${codec?.mimeType ?? inbound.codecId ?? 'unknown'} packets=${inbound.packetsReceived ?? 0} bytes=${inbound.bytesReceived ?? 0} samples=${inbound.totalSamplesReceived ?? 0} energy=${inbound.totalAudioEnergy ?? 0}`;
                  }),
                  `captureChunks=${remoteChunks.length} captureBytes=${remoteChunks.reduce((sum, chunk) => sum + chunk.size, 0)}`
                ].join('\n');
              } catch (error) {
                metricsEl.textContent = 'receiver stats unavailable: ' + error;
              }
            }

            const mark = (id, yes) => document.getElementById(id).classList.toggle('done', Boolean(yes));

            async function readState() {
              try {
                const state = await fetch('/state', { cache: 'no-store' }).then((response) => response.json());
                statusEl.textContent = state.detail;
                detailEl.textContent = [
                  state.codexVersion,
                  state.authMode ? 'auth=' + state.authMode : null,
                  state.attestationMode ? 'attestation=' + state.attestationMode : null,
                  state.threadTitle,
                  state.threadId,
                  state.error
                ].filter(Boolean).join(' · ');
                mark('attestation', state.attestationRequested);
                mark('task', Boolean(state.threadId));
                mark('started', state.realtimeStarted);
                mark('sdp', state.sdpReceived);
                mark('media', state.mediaConnected);
                mark('fixture', state.fixturePlayed);
                mark('remoteTrack', state.remoteTrackReceived);
                mark('audio', state.audioReceived);
                mark('capture', state.captureWritten);
                mark('closed', state.closed);
                captureAvailable = state.captureAvailable;
                fixtureButton.disabled = !state.mediaConnected || !state.fixtureAvailable || state.fixturePlayed;
                if (state.closed && !startingPeer) closePeer();
                return state;
              } catch (error) {
                statusEl.textContent = 'Could not read host state: ' + error;
              }
            }

            function closePeer() {
              startingPeer = false;
              if (pc) { pc.close(); pc = undefined; }
              if (localStream) { localStream.getTracks().forEach((track) => track.stop()); localStream = undefined; }
              if (syntheticOscillator) { syntheticOscillator.stop(); syntheticOscillator = undefined; }
              if (fixtureSource) { try { fixtureSource.stop(); } catch {} fixtureSource = undefined; }
              if (remoteAudioElement) { remoteAudioElement.pause(); remoteAudioElement.srcObject = null; remoteAudioElement = undefined; }
              if (remoteRecorder?.state === 'recording') remoteRecorder.stop();
              startButton.disabled = false;
              fixtureButton.disabled = true;
              captureButton.disabled = true;
              microphoneButton.disabled = true;
              stopButton.disabled = true;
            }

            function createSyntheticInput() {
              syntheticDestination = audioContext.createMediaStreamDestination();
              const gain = audioContext.createGain();
              gain.gain.value = 0;
              syntheticOscillator = audioContext.createOscillator();
              syntheticOscillator.connect(gain);
              gain.connect(syntheticDestination);
              syntheticOscillator.start();
              return syntheticDestination.stream;
            }

            function watchRemoteAudio(stream) {
              fetch('/remote-track', { method: 'POST' });
              remoteAudioElement = document.createElement('audio');
              remoteAudioElement.autoplay = true;
              remoteAudioElement.srcObject = stream;
              remoteAudioElement.play().catch((error) => console.warn('Remote audio playback failed', error));
              const source = audioContext.createMediaStreamSource(stream);
              const analyser = audioContext.createAnalyser();
              analyser.fftSize = 1024;
              source.connect(analyser);
              analyser.connect(audioContext.destination);
              const samples = new Float32Array(analyser.fftSize);

              const sample = () => {
                if (!pc || pc.connectionState === 'closed') return;
                analyser.getFloatTimeDomainData(samples);
                const rms = Math.sqrt(samples.reduce((sum, value) => sum + value * value, 0) / samples.length);
                if (!audioReported && rms > 0.008) {
                  audioReported = true;
                  fetch('/audio-received', { method: 'POST' });
                }
                requestAnimationFrame(sample);
              };
              sample();

              if (!captureAvailable) return;
              remoteChunks = [];
              const recorderOptions = MediaRecorder.isTypeSupported('audio/webm;codecs=opus')
                ? { mimeType: 'audio/webm;codecs=opus' }
                : undefined;
              remoteRecorder = new MediaRecorder(stream, recorderOptions);
              remoteRecorder.ondataavailable = (event) => {
                if (event.data.size > 0) remoteChunks.push(event.data);
              };
              captureCompletion = new Promise((resolve, reject) => {
                remoteRecorder.onstop = async () => {
                  try {
                    const capture = new Blob(remoteChunks, { type: remoteRecorder.mimeType || 'audio/webm' });
                    const response = await fetch('/remote-audio', {
                      method: 'POST',
                      headers: { 'Content-Type': capture.type },
                      body: capture
                    });
                    if (!response.ok) throw new Error(await response.text());
                    resolve();
                  } catch (error) {
                    reject(error);
                  }
                };
              });
              remoteRecorder.start(250);
              captureButton.disabled = false;
            }

            async function stopRemoteCapture() {
              if (!remoteRecorder || remoteRecorder.state === 'inactive') return;
              remoteRecorder.stop();
              captureButton.disabled = true;
              await captureCompletion;
            }

            startButton.onclick = async () => {
              startingPeer = true;
              startButton.disabled = true;
              statusEl.textContent = 'Creating the WebRTC offer…';
              try {
                audioReported = false;
                audioContext = audioContext || new AudioContext();
                await audioContext.resume();
                localStream = createSyntheticInput();

                pc = new RTCPeerConnection();
                localStream.getTracks().forEach((track) => pc.addTrack(track, localStream));
                const events = pc.createDataChannel('oai-events');
                events.onmessage = (event) => console.debug('[oai-events]', event.data);
                pc.ontrack = (event) => watchRemoteAudio(event.streams[0]);
                pc.onconnectionstatechange = async () => {
                  statusEl.textContent = 'WebRTC: ' + pc.connectionState;
                  if (pc.connectionState === 'connected') {
                    startingPeer = false;
                    stopButton.disabled = false;
                    microphoneButton.disabled = false;
                    await fetch('/media-connected', { method: 'POST' });
                  }
                };

                const offer = await pc.createOffer();
                await pc.setLocalDescription(offer);
                await new Promise((resolve) => {
                  if (pc.iceGatheringState === 'complete') { resolve(); return; }
                  pc.addEventListener('icegatheringstatechange', () => {
                    if (pc.iceGatheringState === 'complete') resolve();
                  });
                });

                const response = await fetch('/session', {
                  method: 'POST',
                  headers: { 'Content-Type': 'application/sdp' },
                  body: pc.localDescription.sdp
                });
                if (!response.ok) throw new Error(await response.text());
                await pc.setRemoteDescription({ type: 'answer', sdp: await response.text() });
              } catch (error) {
                statusEl.textContent = 'Canary failed: ' + error;
                closePeer();
              }
            };

            fixtureButton.onclick = async () => {
              fixtureButton.disabled = true;
              statusEl.textContent = 'Loading deterministic microphone fixture…';
              try {
                const response = await fetch('/fixture.wav', { cache: 'no-store' });
                if (!response.ok) throw new Error(await response.text());
                const fixture = await audioContext.decodeAudioData(await response.arrayBuffer());
                fixtureSource = audioContext.createBufferSource();
                fixtureSource.buffer = fixture;
                fixtureSource.connect(syntheticDestination);
                fixtureSource.onended = () => {
                  statusEl.textContent = 'Microphone fixture completed; waiting for model response…';
                  fixtureSource = undefined;
                };
                fixtureSource.start();
                await fetch('/fixture-played', { method: 'POST' });
                statusEl.textContent = 'Playing deterministic microphone fixture…';
              } catch (error) {
                statusEl.textContent = 'Microphone fixture failed: ' + error;
                fixtureButton.disabled = false;
              }
            };

            captureButton.onclick = async () => {
              captureButton.disabled = true;
              statusEl.textContent = 'Saving the remote model-audio track…';
              try {
                await stopRemoteCapture();
                await readState();
              } catch (error) {
                statusEl.textContent = 'Remote capture failed: ' + error;
              }
            };

            microphoneButton.onclick = async () => {
              microphoneButton.disabled = true;
              statusEl.textContent = 'Requesting live microphone access…';
              try {
                const microphone = await navigator.mediaDevices.getUserMedia({
                  audio: { echoCancellation: true, noiseSuppression: true, autoGainControl: true }
                });
                const sender = pc.getSenders().find((candidate) => candidate.track?.kind === 'audio');
                if (!sender) throw new Error('No WebRTC audio sender is active.');
                await sender.replaceTrack(microphone.getAudioTracks()[0]);
                localStream.getTracks().forEach((track) => track.stop());
                localStream = microphone;
                statusEl.textContent = 'Live microphone attached.';
              } catch (error) {
                statusEl.textContent = 'Microphone attach failed: ' + error;
                microphoneButton.disabled = false;
              }
            };

            stopButton.onclick = async () => {
              stopButton.disabled = true;
              try { await stopRemoteCapture(); } catch (error) { console.warn('Remote capture failed', error); }
              await fetch('/stop', { method: 'POST' });
              await readState();
            };

            document.getElementById('refresh').onclick = readState;
            setInterval(readState, 750);
            setInterval(updateReceiverStats, 500);
            readState();
          </script>
        </body>
        </html>
        """;
}
