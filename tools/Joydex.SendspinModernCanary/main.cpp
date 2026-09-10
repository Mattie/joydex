#include <sendspin/client.h>
#include <sendspin/player_role.h>

#include <atomic>
#include <array>
#include <chrono>
#include <condition_variable>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <deque>
#include <mutex>
#include <string>
#include <thread>

namespace {

using namespace std::chrono_literals;

int64_t monotonic_time_us() {
    return std::chrono::duration_cast<std::chrono::microseconds>(
               std::chrono::steady_clock::now().time_since_epoch())
        .count();
}

struct QueuedFrames {
    uint32_t frames;
    uint32_t sample_rate;
};

/// Simulates a bounded realtime speaker and reports completed playback to the
/// modern Sendspin player. This exercises its clock, decoder, and sync task
/// without depending on a workstation audio device.
class RealtimeSink final : public sendspin::PlayerRoleListener {
public:
    explicit RealtimeSink(sendspin::PlayerRole& player) : player_(player), worker_([this] { run(); }) {}

    ~RealtimeSink() override {
        {
            std::lock_guard lock(mutex_);
            stopping_ = true;
        }
        changed_.notify_all();
        worker_.join();
    }

    size_t on_audio_write(uint8_t* data, size_t length, uint32_t timeout_ms) override {
        const auto& params = player_.get_current_stream_params();
        if (!params.codec || *params.codec != sendspin::SendspinCodecFormat::OPUS ||
            !params.sample_rate || *params.sample_rate != 48'000 ||
            !params.channels || *params.channels != 1 ||
            !params.bit_depth || *params.bit_depth != 16) {
            format_mismatches_.fetch_add(1, std::memory_order_relaxed);
            return 0;
        }

        const size_t bytes_per_frame = static_cast<size_t>(*params.channels) * 2U;
        if (bytes_per_frame == 0 || length % bytes_per_frame != 0) {
            return 0;
        }

        const auto frames = static_cast<uint32_t>(length / bytes_per_frame);
        uint64_t audible_frames = 0;
        uint64_t content_frames = 0;
        uint64_t energy = 0;
        for (size_t offset = 0; offset < length; offset += bytes_per_frame) {
            if (data[offset] != 0 || data[offset + 1] != 0) {
                ++audible_frames;
            }
            const auto raw = static_cast<uint16_t>(data[offset]) |
                             (static_cast<uint16_t>(data[offset + 1]) << 8U);
            const auto sample = static_cast<int16_t>(raw);
            const auto magnitude = sample < 0 ? static_cast<uint64_t>(-static_cast<int32_t>(sample))
                                              : static_cast<uint64_t>(sample);
            energy += magnitude;
            if (magnitude >= CONTENT_SAMPLE_THRESHOLD) {
                ++content_frames;
            }
        }
        const auto deadline = std::chrono::steady_clock::now() + std::chrono::milliseconds(timeout_ms);
        std::unique_lock lock(mutex_);
        if (!changed_.wait_until(lock, deadline, [this, frames] {
                return stopping_ || pending_frames_ + frames <= MAX_PENDING_FRAMES;
            })) {
            write_timeouts_.fetch_add(1, std::memory_order_relaxed);
            return 0;
        }
        if (stopping_) {
            return 0;
        }

        queue_.push_back({frames, *params.sample_rate});
        pending_frames_ += frames;
        writes_.fetch_add(1, std::memory_order_relaxed);
        accepted_frames_.fetch_add(frames, std::memory_order_relaxed);
        audible_frames_.fetch_add(audible_frames, std::memory_order_relaxed);
        const int32_t stream_index = current_stream_index_.load(std::memory_order_acquire);
        if (stream_index >= 0 && static_cast<size_t>(stream_index) < MAX_STREAMS) {
            stream_audible_frames_[stream_index].fetch_add(audible_frames, std::memory_order_relaxed);
            stream_energy_[stream_index].fetch_add(energy, std::memory_order_relaxed);
        } else if (audible_frames > 0) {
            unattributed_audible_frames_.fetch_add(audible_frames, std::memory_order_relaxed);
        }
        if (content_frames * 4 >= frames * 3) {
            int32_t segment_index = current_content_segment_.load(std::memory_order_relaxed);
            if (segment_index < 0 || silent_content_gap_frames_ >= CONTENT_SEGMENT_GAP_FRAMES) {
                segment_index = static_cast<int32_t>(
                    content_segments_.fetch_add(1, std::memory_order_relaxed));
                current_content_segment_.store(segment_index, std::memory_order_relaxed);
            }
            if (segment_index >= 0 && static_cast<size_t>(segment_index) < MAX_SEGMENTS) {
                segment_audible_frames_[segment_index].fetch_add(audible_frames, std::memory_order_relaxed);
                segment_energy_[segment_index].fetch_add(energy, std::memory_order_relaxed);
            }
            silent_content_gap_frames_ = 0;
        } else if (current_content_segment_.load(std::memory_order_relaxed) >= 0) {
            silent_content_gap_frames_ += frames;
        }
        changed_.notify_all();
        return length;
    }

    void on_stream_start() override {
        const uint32_t stream_index = stream_starts_.fetch_add(1, std::memory_order_relaxed);
        current_stream_index_.store(static_cast<int32_t>(stream_index), std::memory_order_release);
    }

    void on_stream_end() override {
        stream_ends_.fetch_add(1, std::memory_order_relaxed);
        current_stream_index_.store(-1, std::memory_order_release);
        ended_at_us_.store(monotonic_time_us(), std::memory_order_release);
    }

    uint64_t writes() const { return writes_.load(std::memory_order_relaxed); }
    uint64_t accepted_frames() const { return accepted_frames_.load(std::memory_order_relaxed); }
    uint64_t played_frames() const { return played_frames_.load(std::memory_order_relaxed); }
    uint64_t write_timeouts() const { return write_timeouts_.load(std::memory_order_relaxed); }
    uint64_t audible_frames() const { return audible_frames_.load(std::memory_order_relaxed); }
    uint64_t format_mismatches() const { return format_mismatches_.load(std::memory_order_relaxed); }
    uint64_t unattributed_audible_frames() const {
        return unattributed_audible_frames_.load(std::memory_order_relaxed);
    }
    uint64_t stream_audible_frames(size_t index) const {
        return stream_audible_frames_[index].load(std::memory_order_relaxed);
    }
    uint64_t stream_energy(size_t index) const {
        return stream_energy_[index].load(std::memory_order_relaxed);
    }
    uint32_t content_segments() const { return content_segments_.load(std::memory_order_relaxed); }
    uint64_t segment_audible_frames(size_t index) const {
        return segment_audible_frames_[index].load(std::memory_order_relaxed);
    }
    uint64_t segment_energy(size_t index) const {
        return segment_energy_[index].load(std::memory_order_relaxed);
    }
    uint32_t stream_starts() const { return stream_starts_.load(std::memory_order_relaxed); }
    uint32_t stream_ends() const { return stream_ends_.load(std::memory_order_relaxed); }
    int64_t ended_at_us() const { return ended_at_us_.load(std::memory_order_acquire); }

private:
    void run() {
        while (true) {
            QueuedFrames item{};
            {
                std::unique_lock lock(mutex_);
                changed_.wait(lock, [this] { return stopping_ || !queue_.empty(); });
                if (stopping_ && queue_.empty()) {
                    return;
                }
                item = queue_.front();
                queue_.pop_front();
            }

            const auto duration = std::chrono::microseconds(
                static_cast<int64_t>(item.frames) * 1'000'000LL / item.sample_rate);
            std::this_thread::sleep_for(duration);
            player_.notify_audio_played(item.frames, monotonic_time_us());
            played_frames_.fetch_add(item.frames, std::memory_order_relaxed);

            {
                std::lock_guard lock(mutex_);
                pending_frames_ -= item.frames;
            }
            changed_.notify_all();
        }
    }

    // The server intentionally schedules one second ahead. Leave a second
    // window beyond that lead so ordinary host-thread jitter cannot turn the
    // fake sink into the bottleneck under test.
    static constexpr uint32_t MAX_PENDING_FRAMES = 96'000;
    static constexpr size_t MAX_STREAMS = 8;
    static constexpr size_t MAX_SEGMENTS = 8;
    static constexpr uint64_t CONTENT_SEGMENT_GAP_FRAMES = 24'000;
    static constexpr uint64_t CONTENT_SAMPLE_THRESHOLD = 256;

    sendspin::PlayerRole& player_;
    std::thread worker_;
    std::mutex mutex_;
    std::condition_variable changed_;
    std::deque<QueuedFrames> queue_;
    uint32_t pending_frames_{0};
    bool stopping_{false};
    std::atomic<uint64_t> writes_{0};
    std::atomic<uint64_t> accepted_frames_{0};
    std::atomic<uint64_t> played_frames_{0};
    std::atomic<uint64_t> write_timeouts_{0};
    std::atomic<uint64_t> audible_frames_{0};
    std::atomic<uint64_t> format_mismatches_{0};
    std::atomic<uint64_t> unattributed_audible_frames_{0};
    std::array<std::atomic<uint64_t>, MAX_STREAMS> stream_audible_frames_{};
    std::array<std::atomic<uint64_t>, MAX_STREAMS> stream_energy_{};
    std::array<std::atomic<uint64_t>, MAX_SEGMENTS> segment_audible_frames_{};
    std::array<std::atomic<uint64_t>, MAX_SEGMENTS> segment_energy_{};
    std::atomic<uint32_t> stream_starts_{0};
    std::atomic<uint32_t> stream_ends_{0};
    std::atomic<uint32_t> content_segments_{0};
    std::atomic<int32_t> current_stream_index_{-1};
    std::atomic<int32_t> current_content_segment_{-1};
    uint64_t silent_content_gap_frames_{0};
    std::atomic<int64_t> ended_at_us_{0};
};

class NetworkProvider final : public sendspin::SendspinNetworkProvider {
public:
    bool is_network_ready() override { return true; }
};

class ClientListener final : public sendspin::SendspinClientListener {
public:
    void on_time_sync_updated(float) override { updates.fetch_add(1, std::memory_order_relaxed); }

    std::atomic<uint32_t> updates{0};
};

struct Options {
    uint16_t port{8927};
    uint64_t minimum_audible_frames{0};
    uint64_t minimum_audible_frames_per_stream{0};
    uint64_t minimum_audible_frames_per_segment{0};
    uint32_t required_streams{1};
    uint32_t required_content_segments{0};
    bool require_rising_stream_energy{false};
    bool require_rising_segment_energy{false};
};

Options read_options(int argc, char** argv) {
    Options options;
    for (int index = 1; index < argc; index += 2) {
        if (index + 1 >= argc) {
            std::fprintf(stderr,
                         "Usage: %s [--port 1..65535] [--required-streams 1..8] "
                         "[--minimum-audible-frames N] [--minimum-audible-frames-per-stream N] "
                         "[--required-content-segments 0..8] "
                         "[--minimum-audible-frames-per-segment N] "
                         "[--require-rising-stream-energy 0|1] "
                         "[--require-rising-segment-energy 0|1]\n",
                         argv[0]);
            std::exit(2);
        }
        const std::string name(argv[index]);
        const unsigned long long value = std::strtoull(argv[index + 1], nullptr, 10);
        if (name == "--port" && value >= 1 && value <= 65535) {
            options.port = static_cast<uint16_t>(value);
        } else if (name == "--minimum-audible-frames") {
            options.minimum_audible_frames = value;
        } else if (name == "--minimum-audible-frames-per-stream") {
            options.minimum_audible_frames_per_stream = value;
        } else if (name == "--minimum-audible-frames-per-segment") {
            options.minimum_audible_frames_per_segment = value;
        } else if (name == "--required-streams" && value >= 1 && value <= 8) {
            options.required_streams = static_cast<uint32_t>(value);
        } else if (name == "--required-content-segments" && value <= 8) {
            options.required_content_segments = static_cast<uint32_t>(value);
        } else if (name == "--require-rising-stream-energy" && value <= 1) {
            options.require_rising_stream_energy = value == 1;
        } else if (name == "--require-rising-segment-energy" && value <= 1) {
            options.require_rising_segment_energy = value == 1;
        } else {
            std::fprintf(stderr,
                         "Usage: %s [--port 1..65535] [--required-streams 1..8] "
                         "[--minimum-audible-frames N] [--minimum-audible-frames-per-stream N] "
                         "[--required-content-segments 0..8] "
                         "[--minimum-audible-frames-per-segment N] "
                         "[--require-rising-stream-energy 0|1] "
                         "[--require-rising-segment-energy 0|1]\n",
                         argv[0]);
            std::exit(2);
        }
    }
    return options;
}

}  // namespace

int main(int argc, char** argv) {
    const Options options = read_options(argc, argv);
    const uint16_t port = options.port;
    sendspin::SendspinClient::set_log_level(sendspin::LogLevel::INFO);

    sendspin::SendspinClientConfig client_config;
    client_config.client_id = "joydex-modern-canary";
    client_config.name = "Joydex Modern Sendspin Canary";
    client_config.product_name = "Joydex host compatibility harness";
    client_config.manufacturer = "Joydex";
    client_config.software_version = "sendspin-cpp-0.7.2";
    client_config.server_port = port;

    sendspin::SendspinClient client(std::move(client_config));
    sendspin::PlayerRoleConfig player_config;
    player_config.audio_formats = {
        {sendspin::SendspinCodecFormat::OPUS, 1, 48'000, 16},
        {sendspin::SendspinCodecFormat::PCM, 1, 48'000, 16},
    };
    auto& player = client.add_player(std::move(player_config));

    RealtimeSink sink(player);
    NetworkProvider network;
    ClientListener listener;
    player.set_listener(&sink);
    client.set_listener(&listener);
    client.set_network_provider(&network);

    if (!client.start_server()) {
        std::fprintf(stderr, "Failed to start modern Sendspin canary on port %u\n", port);
        return 1;
    }

    const auto deadline = std::chrono::steady_clock::now() + 45s;
    while (std::chrono::steady_clock::now() < deadline) {
        client.loop();
        if (sink.stream_ends() >= options.required_streams && sink.ended_at_us() != 0 &&
            monotonic_time_us() - sink.ended_at_us() >= 750'000) {
            break;
        }
        std::this_thread::sleep_for(10ms);
    }

    bool stream_content_accepted = true;
    for (uint32_t index = 0; index < options.required_streams; ++index) {
        stream_content_accepted = stream_content_accepted &&
                                  sink.stream_audible_frames(index) >=
                                      options.minimum_audible_frames_per_stream;
        if (options.require_rising_stream_energy && index > 0) {
            stream_content_accepted = stream_content_accepted &&
                                      sink.stream_energy(index) > sink.stream_energy(index - 1);
        }
    }
    bool segment_content_accepted = options.required_content_segments == 0 ||
                                    sink.content_segments() == options.required_content_segments;
    for (uint32_t index = 0; index < options.required_content_segments; ++index) {
        segment_content_accepted = segment_content_accepted &&
                                   sink.segment_audible_frames(index) >=
                                       options.minimum_audible_frames_per_segment;
        if (options.require_rising_segment_energy && index > 0) {
            segment_content_accepted = segment_content_accepted &&
                                       sink.segment_energy(index) > sink.segment_energy(index - 1);
        }
    }
    const bool accepted = sink.stream_starts() == options.required_streams &&
                          sink.stream_ends() == options.required_streams
                          && sink.writes() > 0 && sink.accepted_frames() == sink.played_frames()
                          && sink.write_timeouts() == 0 && sink.format_mismatches() == 0
                          && sink.audible_frames() >= options.minimum_audible_frames
                          && sink.unattributed_audible_frames() == 0 && stream_content_accepted
                          && segment_content_accepted
                          && listener.updates.load(std::memory_order_relaxed) > 0;
    std::printf(
        "{\"accepted\":%s,\"port\":%u,\"streamStarts\":%u,\"streamEnds\":%u,"
        "\"writes\":%llu,\"acceptedFrames\":%llu,\"playedFrames\":%llu,"
        "\"writeTimeouts\":%llu,\"audibleFrames\":%llu,\"minimumAudibleFrames\":%llu,"
        "\"minimumAudibleFramesPerStream\":%llu,\"unattributedAudibleFrames\":%llu,"
        "\"formatMismatches\":%llu,\"timeSyncUpdates\":%u,\"streamAudibleFrames\":[",
        accepted ? "true" : "false", port, sink.stream_starts(), sink.stream_ends(),
        static_cast<unsigned long long>(sink.writes()),
        static_cast<unsigned long long>(sink.accepted_frames()),
        static_cast<unsigned long long>(sink.played_frames()),
        static_cast<unsigned long long>(sink.write_timeouts()),
        static_cast<unsigned long long>(sink.audible_frames()),
        static_cast<unsigned long long>(options.minimum_audible_frames),
        static_cast<unsigned long long>(options.minimum_audible_frames_per_stream),
        static_cast<unsigned long long>(sink.unattributed_audible_frames()),
        static_cast<unsigned long long>(sink.format_mismatches()),
        listener.updates.load(std::memory_order_relaxed));
    for (uint32_t index = 0; index < options.required_streams; ++index) {
        std::printf("%s%llu", index == 0 ? "" : ",",
                    static_cast<unsigned long long>(sink.stream_audible_frames(index)));
    }
    std::printf("],\"streamEnergy\":[");
    for (uint32_t index = 0; index < options.required_streams; ++index) {
        std::printf("%s%llu", index == 0 ? "" : ",",
                    static_cast<unsigned long long>(sink.stream_energy(index)));
    }
    std::printf("],\"requiredStreams\":%u,\"risingStreamEnergyRequired\":%s,"
                "\"contentSegments\":%u,\"segmentAudibleFrames\":[",
                options.required_streams,
                options.require_rising_stream_energy ? "true" : "false",
                sink.content_segments());
    for (uint32_t index = 0; index < options.required_content_segments; ++index) {
        std::printf("%s%llu", index == 0 ? "" : ",",
                    static_cast<unsigned long long>(sink.segment_audible_frames(index)));
    }
    std::printf("],\"segmentEnergy\":[");
    for (uint32_t index = 0; index < options.required_content_segments; ++index) {
        std::printf("%s%llu", index == 0 ? "" : ",",
                    static_cast<unsigned long long>(sink.segment_energy(index)));
    }
    std::printf("],\"requiredContentSegments\":%u,"
                "\"minimumAudibleFramesPerSegment\":%llu,"
                "\"risingSegmentEnergyRequired\":%s}\n",
                options.required_content_segments,
                static_cast<unsigned long long>(options.minimum_audible_frames_per_segment),
                options.require_rising_segment_energy ? "true" : "false");
    return accepted ? 0 : 1;
}
