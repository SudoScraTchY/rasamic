#include "rasamic_native.h"

#include "miniaudio.h"

#include <atomic>

namespace {

struct Capture {
    ma_context ctx;
    ma_device dev;
    rm_pcm_callback cb;
    void* user;
    bool ctxReady = false;
    bool devReady = false;
};

void DataCallback(ma_device* dev, void* /*output*/, const void* input, ma_uint32 frameCount) {
    auto* c = static_cast<Capture*>(dev->pUserData);
    if (input)
        c->cb(c->user, static_cast<const int16_t*>(input), static_cast<int32_t>(frameCount * dev->capture.channels));
}

} // namespace

extern "C" {

void* rm_capture_create(int32_t sample_rate, int32_t channels, rm_pcm_callback cb, void* user) {
    if (!cb) return nullptr;
    auto* c = new Capture();
    c->cb = cb;
    c->user = user;
    if (ma_context_init(nullptr, 0, nullptr, &c->ctx) != MA_SUCCESS) {
        delete c;
        return nullptr;
    }
    c->ctxReady = true;

    ma_device_config cfg = ma_device_config_init(ma_device_type_capture);
    cfg.capture.format = ma_format_s16;
    cfg.capture.channels = static_cast<ma_uint32>(channels);
    cfg.sampleRate = static_cast<ma_uint32>(sample_rate);
    cfg.dataCallback = DataCallback;
    cfg.pUserData = c;
    cfg.periodSizeInMilliseconds = 10;

    if (ma_device_init(&c->ctx, &cfg, &c->dev) != MA_SUCCESS) {
        ma_context_uninit(&c->ctx);
        delete c;
        return nullptr;
    }
    c->devReady = true;
    return c;
}

int32_t rm_capture_start(void* h) {
    auto* c = static_cast<Capture*>(h);
    if (!c) return -1;
    return ma_device_start(&c->dev) == MA_SUCCESS ? 0 : -1;
}

void rm_capture_stop(void* h) {
    auto* c = static_cast<Capture*>(h);
    if (c && c->devReady) ma_device_stop(&c->dev);
}

void rm_capture_destroy(void* h) {
    auto* c = static_cast<Capture*>(h);
    if (!c) return;
    if (c->devReady) {
        ma_device_stop(&c->dev);
        ma_device_uninit(&c->dev);
    }
    if (c->ctxReady)
        ma_context_uninit(&c->ctx);
    delete c;
}

} /* extern "C" */