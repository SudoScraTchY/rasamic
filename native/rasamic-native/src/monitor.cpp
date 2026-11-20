#include "rasamic_native.h"

#define MINIAUDIO_IMPLEMENTATION
#include "miniaudio.h"

#include <atomic>
#include <cstring>

namespace {

constexpr int kRingSamples = 96000; /* 2 s at 48k mono */

struct Monitor {
    ma_context ctx;
    ma_device dev;
    float ring[kRingSamples];
    std::atomic<uint32_t> head{0};
    std::atomic<uint32_t> tail{0};
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
    if (written < total)
        std::memset(out + written, 0, (total - written) * sizeof(float));
}

} // namespace

extern "C" {

static void* CreateMonitor(int32_t sample_rate, int32_t channels, const char* name_filter) {
    auto* m = new Monitor();
    ma_result ctxResult;
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

    ma_device_id selectedId;
    const ma_device_id* selected = nullptr;
    if (name_filter && name_filter[0]) {
        ma_device_info* playback = nullptr;
        ma_uint32 playbackCount = 0;
        ma_device_info* capture = nullptr;
        ma_uint32 captureCount = 0;
        if (ma_context_get_devices(&m->ctx, &playback, &playbackCount, &capture, &captureCount) == MA_SUCCESS) {
            for (ma_uint32 i = 0; i < playbackCount; ++i) {
                if (std::strstr(playback[i].name, name_filter)) {
                    selectedId = playback[i].id;
                    selected = &selectedId;
                    break;
                }
            }
        }
        if (!selected) {
            ma_context_uninit(&m->ctx);
            delete m;
            return nullptr;
        }
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
    return CreateMonitor(sample_rate, channels, nullptr);
}

void* rm_monitor_create_named(int32_t sample_rate, int32_t channels, const char* name_filter) {
    return CreateMonitor(sample_rate, channels, name_filter);
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
    return written;
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