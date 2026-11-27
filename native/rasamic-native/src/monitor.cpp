#include "rasamic_native.h"

#define MINIAUDIO_IMPLEMENTATION
#include "miniaudio.h"

#include <atomic>
#include <cstring>

namespace {

constexpr int kRingSamples = 2400; /* at most 50 ms at 48k mono */

struct Monitor {
    ma_context ctx;
    ma_device dev;
    float ring[kRingSamples];
    std::atomic<uint32_t> head{0};
    std::atomic<uint32_t> tail{0};
    std::atomic<uint64_t> dropped{0};
    std::atomic<uint64_t> underruns{0};
    bool ctxReady = false;
    bool devReady = false;
};

void DataCallback(ma_device* dev, void* output, const void* /*input*/, ma_uint32 frameCount) {
    auto* m = static_cast<Monitor*>(dev->pUserData);
    auto* out = static_cast<float*>(output);
    const ma_uint32 channels = dev->playback.channels;
    const ma_uint32 total = frameCount * channels;
    uint32_t tail = m->tail.load(std::memory_order_relaxed);
    const uint32_t head = m->head.load(std::memory_order_acquire);
    ma_uint32 written = 0;
    while (written < total && tail != head) {
        out[written++] = m->ring[tail];
        tail = (tail + 1) % kRingSamples;
    }
    m->tail.store(tail, std::memory_order_release);
    if (written < total) {
        m->underruns.fetch_add(total - written, std::memory_order_relaxed);
        std::memset(out + written, 0, (total - written) * sizeof(float));
    }
}

} // namespace

extern "C" {

static void* CreateMonitor(int32_t sample_rate, int32_t channels, const char* name_filter, const char* device_id) {
    auto* m = new Monitor();
    ma_result ctxResult;
#if defined(_WIN32)
    if (device_id && device_id[0]) {
        ma_backend backends[] = { ma_backend_wasapi };
        ctxResult = ma_context_init(backends, 1, nullptr, &m->ctx);
    } else
#endif
#if defined(__linux__) && !defined(__ANDROID__)
    if (name_filter && name_filter[0]) {
        /* Virtual-mic sinks live in PulseAudio/pipewire-pulse; ALSA may enumerate
         * zero devices on headless machines, so try Pulse first. */
        ma_backend backends[] = { ma_backend_pulseaudio, ma_backend_alsa, ma_backend_jack };
        ctxResult = ma_context_init(backends, 3, nullptr, &m->ctx);
    } else
#endif
    {
        ctxResult = ma_context_init(nullptr, 0, nullptr, &m->ctx);
    }
    if (ctxResult != MA_SUCCESS) {
        delete m;
        return nullptr;
    }
    m->ctxReady = true;

    ma_device_id selectedId{};
    const ma_device_id* selected = nullptr;
    if (device_id && device_id[0]) {
#if defined(_WIN32)
        const int converted = MultiByteToWideChar(CP_UTF8, 0, device_id, -1,
            selectedId.wasapi, static_cast<int>(sizeof(selectedId.wasapi) / sizeof(selectedId.wasapi[0])));
        if (converted <= 0 || converted > static_cast<int>(sizeof(selectedId.wasapi) / sizeof(selectedId.wasapi[0]))) {
            ma_context_uninit(&m->ctx);
            delete m;
            return nullptr;
        }
        selected = &selectedId;
#else
        ma_device_info* playback = nullptr;
        ma_uint32 playbackCount = 0;
        if (ma_context_get_devices(&m->ctx, &playback, &playbackCount, nullptr, nullptr) == MA_SUCCESS) {
            for (ma_uint32 i = 0; i < playbackCount; ++i) {
                if (std::strcmp(playback[i].name, device_id) == 0) {
                    selectedId = playback[i].id;
                    selected = &selectedId;
                    break;
                }
            }
        }
#endif
    } else if (name_filter && name_filter[0]) {
        ma_device_info* playback = nullptr;
        ma_uint32 playbackCount = 0;
        ma_device_info* capture = nullptr;
        ma_uint32 captureCount = 0;
        if (ma_context_get_devices(&m->ctx, &playback, &playbackCount, &capture, &captureCount) == MA_SUCCESS) {
            for (ma_uint32 i = 0; i < playbackCount; ++i) {
                if (std::strcmp(playback[i].name, name_filter) == 0) {
                    selectedId = playback[i].id;
                    selected = &selectedId;
                    break;
                }
            }
            if (!selected) {
                for (ma_uint32 i = 0; i < playbackCount; ++i) {
                    if (std::strstr(playback[i].name, name_filter)) {
                        selectedId = playback[i].id;
                        selected = &selectedId;
                        break;
                    }
                }
            }
        }
    }
    if (!selected && ((name_filter && name_filter[0]) || (device_id && device_id[0]))) {
        ma_context_uninit(&m->ctx);
        delete m;
        return nullptr;
    }

    ma_device_config cfg = ma_device_config_init(ma_device_type_playback);
    cfg.playback.pDeviceID = selected;
    cfg.playback.format = ma_format_f32;
    cfg.playback.channels = static_cast<ma_uint32>(channels);
    cfg.sampleRate = static_cast<ma_uint32>(sample_rate);
    cfg.dataCallback = DataCallback;
    cfg.pUserData = m;
    cfg.periodSizeInMilliseconds = 10;

    if (ma_device_init(&m->ctx, &cfg, &m->dev) != MA_SUCCESS) {
        ma_context_uninit(&m->ctx);
        delete m;
        return nullptr;
    }
    m->devReady = true;
    if (ma_device_start(&m->dev) != MA_SUCCESS) {
        ma_device_uninit(&m->dev);
        ma_context_uninit(&m->ctx);
        delete m;
        return nullptr;
    }
    return m;
}

void* rm_monitor_create(int32_t sample_rate, int32_t channels) {
    return CreateMonitor(sample_rate, channels, nullptr, nullptr);
}

void* rm_monitor_create_named(int32_t sample_rate, int32_t channels, const char* name_filter) {
    return CreateMonitor(sample_rate, channels, name_filter, nullptr);
}

void* rm_monitor_create_id(int32_t sample_rate, int32_t channels, const char* device_id) {
    return CreateMonitor(sample_rate, channels, nullptr, device_id);
}

static int32_t EnumerateDevices(int32_t device_type, rm_device_callback callback, void* user) {
    if (!callback || (device_type != RM_DEVICE_PLAYBACK && device_type != RM_DEVICE_CAPTURE))
        return -1;

    ma_context ctx;
    ma_result ctxResult;
#if defined(_WIN32)
    ma_backend backends[] = { ma_backend_wasapi };
    ctxResult = ma_context_init(backends, 1, nullptr, &ctx);
#elif defined(__linux__) && !defined(__ANDROID__)
    ma_backend backends[] = { ma_backend_pulseaudio, ma_backend_alsa, ma_backend_jack };
    ctxResult = ma_context_init(backends, 3, nullptr, &ctx);
#else
    ctxResult = ma_context_init(nullptr, 0, nullptr, &ctx);
#endif
    if (ctxResult != MA_SUCCESS)
        return static_cast<int32_t>(ctxResult);

    ma_device_info* playback = nullptr;
    ma_uint32 playbackCount = 0;
    ma_device_info* capture = nullptr;
    ma_uint32 captureCount = 0;
    if (ma_context_get_devices(&ctx, &playback, &playbackCount, &capture, &captureCount) != MA_SUCCESS) {
        ma_context_uninit(&ctx);
        return -2;
    }

    const ma_device_info* devices = device_type == RM_DEVICE_PLAYBACK ? playback : capture;
    const ma_uint32 count = device_type == RM_DEVICE_PLAYBACK ? playbackCount : captureCount;
    for (ma_uint32 i = 0; i < count; ++i) {
        char id[256] = {};
#if defined(_WIN32)
        const char* deviceId = id;
        if (WideCharToMultiByte(CP_UTF8, 0, devices[i].id.wasapi, -1,
                id, static_cast<int>(sizeof(id) / sizeof(id[0])), nullptr, nullptr) == 0)
            deviceId = "";
#else
        const char* deviceId = "";
#endif
        callback(user, deviceId, devices[i].name, devices[i].isDefault ? 1 : 0);
    }

    ma_context_uninit(&ctx);
    return 0;
}

int32_t rm_devices_enumerate(int32_t device_type, rm_device_callback callback, void* user) {
    return EnumerateDevices(device_type, callback, user);
}

int32_t rm_monitor_write(void* h, const float* pcm, int32_t samples) {
    auto* m = static_cast<Monitor*>(h);
    if (!m || !pcm || samples < 0) return -1;
    uint32_t head = m->head.load(std::memory_order_relaxed);
    const uint32_t tail = m->tail.load(std::memory_order_acquire);
    int32_t written = 0;
    for (int32_t i = 0; i < samples; ++i) {
        uint32_t next = (head + 1) % kRingSamples;
        if (next == tail) break; /* full: drop newest */
        m->ring[head] = pcm[i];
        head = next;
        written++;
    }
    m->head.store(head, std::memory_order_release);
    if (written < samples)
        m->dropped.fetch_add(samples - written, std::memory_order_relaxed);
    return written;
}

void rm_monitor_get_stats(void* h, rm_monitor_stats* out) {
    if (!out) return;
    *out = {};
    auto* m = static_cast<Monitor*>(h);
    if (!m) return;
    const uint32_t head = m->head.load(std::memory_order_acquire);
    const uint32_t tail = m->tail.load(std::memory_order_acquire);
    out->capacity_samples = kRingSamples;
    out->queued_samples = (head - tail) % kRingSamples;
    out->dropped_samples = m->dropped.load(std::memory_order_relaxed);
    out->underrun_samples = m->underruns.load(std::memory_order_relaxed);
    out->device_period_frames = m->dev.playback.internalPeriodSizeInFrames;
    out->device_buffer_frames = out->device_period_frames * m->dev.playback.internalPeriods;
#if defined(_WIN32)
    if (m->dev.wasapi.actualBufferSizeInFramesPlayback > 0)
        out->device_buffer_frames = m->dev.wasapi.actualBufferSizeInFramesPlayback;
#endif
    out->device_sample_rate = m->dev.playback.internalSampleRate;
    out->device_channels = m->dev.playback.internalChannels;
}

void rm_monitor_destroy(void* h) {
    auto* m = static_cast<Monitor*>(h);
    if (!m) return;
    if (m->devReady) {
        ma_device_stop(&m->dev);
        ma_device_uninit(&m->dev);
    }
    if (m->ctxReady)
        ma_context_uninit(&m->ctx);
    delete m;
}

/* Polar patterns exist only on the iOS capture backend. */
int32_t rm_capture_set_polar(void*, int32_t) { return -1; }

} /* extern "C" */