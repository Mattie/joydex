# Voice PE turn-three regression

Use this attended conversation after transport sanity checks and before accepting a
Voice PE or Joydex voice-audio deployment. It reproduces the progression in which
playback began clearly and degraded during the third assistant response.

## Conversation

Speak naturally from the normal room position. Do not exaggerate volume or pace.

1. Ask a brief conversational question.
2. Ask for a medium-length explanation of a neutral topic.
3. Ask for a sustained response of at least 30 seconds.
4. While the assistant is still audibly speaking, interrupt with a short correction.
5. Ask at least one ordinary follow-up and confirm its complete response is audible.
6. Continue natural follow-ups until the Voice Session has remained active for at
   least two minutes, then say “Hang up.”

The third prompt is intended to produce a longer sustained response. The exact
assistant wording does not matter; do not substitute a short acknowledgement. If
that response is shorter than 30 seconds (1,500 20 ms host frames), extend or repeat
the request in the same session until one continuous response reaches that duration.

Before wake and after rearm, capture the device's cumulative audio and Sendspin
counters. Preserve the host speaker playout summary and the device log covering the
same interval.

## Acceptance gate

- The wake cue and connected/listening cue remain distinct and correctly timed.
- Every assistant response is intelligible from beginning to end, especially the
  long third response.
- The mid-response interruption is recognized without permanently silencing or
  corrupting later playback; at least one complete assistant response follows it.
- Spoken hangup ends the Voice Session and the endpoint returns to Armed.
- The device does not reboot, reconnect, or remain stuck in its listening LED state.
- Host speaker playout reports no source-frame loss, overflow, or abnormal pacing.
- `Joydex Sendspin Playback Tasks Started` increases by exactly one for each fresh
  Voice Session. A response boundary must not create another player task.
- For legacy firmware that exposes them, starting-to-ending deltas remain zero for
  late Sendspin chunks and initial late chunks. The modern `0.1.33` adapter does
  not expose those legacy counters; use its host queue/overflow/pacing summary,
  microphone diagnostics, reboot evidence, and audible result instead.
- Device logs contain no `Playback info queue was full`, I²S failure, task-watchdog,
  panic, or reset evidence during the session.
- Repeat the complete progression in a second fresh Voice Session. Both runs must pass.

Synthetic tones and prerecorded audio remain useful isolation tests, but they do
not replace this full-duplex, multi-turn acceptance gate.
