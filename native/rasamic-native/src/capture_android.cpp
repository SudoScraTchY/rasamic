#include "rasamic_native.h"

#include <oboe/Oboe.h>

#include <atomic>
#include <memory>

namespace {

class CaptureCallback : public oboe::AudioStreamCallback {
public:
    CaptureCallback(rm_pcm_callback cb, void* user) : cb_(cb), user_(user) {}

    oboe::DataCallbackResult onAudioReady(oboe::AudioStream* stream, void* data, int32_t frames) override {
        const int32_t channels = stream->getChannelCount();
        cb_(user_, static_cast<const int16_t*>(data), frames * channels);
        return oboe::DataCallbackResult::Continue;
    }

    void onErrorAfterClose(oboe::AudioStream* /*stream*/, oboe::Result error) override {
        if (error == oboe::Result::ErrorDisconnected)
            disconnected_.store(true, std::memory_order_relaxed);
    }

    bool disconnected() const { return disconnected_.load(std::memory_order_relaxed); }

private:
    rm_pcm_callback cb_;
    void* user_;
    std::atomic<bool> disconnected_{false};
};

struct Capture {
    std::shared_ptr<oboe::AudioStream> stream;
    std::unique_ptr<CaptureCallback> callback;
};

} // namespace

extern "C" {

void* rm_capture_create(int32_t sample_rate, int32_t channels, rm_pcm_callback cb, void* user) {
    if (!cb) return nullptr;
    auto c = std::make_unique<Capture>();
    c->callback = std::make_unique<CaptureCallback>(cb, user);

    oboe::AudioStreamBuilder builder;
    builder.setDirection(oboe::Direction::Input)
        ->setPerformanceMode(oboe::PerformanceMode::LowLatency)
        ->setSharingMode(oboe::SharingMode::Exclusive)
        ->setFormat(oboe::AudioFormat::I16)
        ->setChannelCount(channels)
        ->setSampleRate(sample_rate)
        ->setInputPreset(oboe::InputPreset::VoiceRecognition)
        ->setFramesPerDataCallback(sample_rate / 100) /* 10 ms */
        ->setCallback(c->callback.get());

    oboe::Result r = builder.openStream(c->stream);
    if (r != oboe::Result::OK) return nullptr;
    /* The DSP/Opus chain is hard-locked to the requested rate; refuse a
     * device that fell back to a different one rather than pitch-shifting. */
    if (c->stream->getSampleRate() != sample_rate ||
        c->stream->getChannelCount() != channels) {
        c->stream->close();
        return nullptr;
    }
    return c.release();
}

int32_t rm_capture_start(void* h) {
    auto* c = static_cast<Capture*>(h);
    if (!c) return -1;
    return c->stream->requestStart() == oboe::Result::OK ? 0 : -1;
}

void rm_capture_stop(void* h) {
    auto* c = static_cast<Capture*>(h);
    if (c) c->stream->requestStop();
}

void rm_capture_destroy(void* h) {
    auto* c = static_cast<Capture*>(h);
    if (!c) return;
    c->stream->close();
    delete c;
}

} /* extern "C" */