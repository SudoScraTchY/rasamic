#include "rasamic_native.h"

#include <opus.h>
#include <rnnoise.h>
#include <speex/speex_preprocess.h>

#include <algorithm>
#include <atomic>
#include <chrono>
#include <cmath>
#include <cstring>

namespace {

constexpr int kSampleRate = 48000;
constexpr int kMaxFrameSamples = 960; /* 20 ms */
constexpr int kRingSlots = 64;
constexpr int kSlotSize = 512;
constexpr float kLimiterThreshold = 28000.0f;
constexpr float kLimiterCeiling = 32767.0f;

struct PacketSlot {
    uint32_t seq;
    uint64_t ts;
    int32_t len;
    uint8_t data[kSlotSize];
};

struct Pipeline {
    OpusEncoder* enc = nullptr;
    DenoiseState* rn[2] = {nullptr, nullptr};
    SpeexPreprocessState* agc[2] = {nullptr, nullptr};
    int channels = 1;
    int frameSamples = 480;

    int16_t acc[kMaxFrameSamples * 2];
    int accFill = 0;
    float chf[2][kMaxFrameSamples];
    int16_t chi[2][kMaxFrameSamples];
    int16_t ibuf[kMaxFrameSamples * 2];

    PacketSlot ring[kRingSlots];
    std::atomic<uint32_t> head{0};
    std::atomic<uint32_t> tail{0};

    std::atomic<float> gainDb{0.0f};
    std::atomic<int> muted{0};
    std::atomic<int> ns{1};
    std::atomic<int> agcOn{1};

    std::atomic<uint64_t> frames{0};
    std::atomic<uint64_t> packets{0};
    std::atomic<uint64_t> dropped{0};

    uint32_t seq = 0;
    uint64_t totalSamples = 0;
    uint64_t baseTsUs = 0;
};

float SoftLimit(float x) {
    if (x > kLimiterThreshold)
        return kLimiterThreshold + (kLimiterCeiling - kLimiterThreshold) *
               tanhf((x - kLimiterThreshold) / (kLimiterCeiling - kLimiterThreshold));
    if (x < -kLimiterThreshold)
        return -kLimiterThreshold - (kLimiterCeiling - kLimiterThreshold) *
               tanhf((-x - kLimiterThreshold) / (kLimiterCeiling - kLimiterThreshold));
    return x;
}

void ProcessFrame(Pipeline* p) {
    const int n = p->frameSamples;
    const int c = p->channels;

    for (int ch = 0; ch < c; ++ch)
        for (int i = 0; i < n; ++i)
            p->chf[ch][i] = static_cast<float>(p->acc[i * c + ch]);

    if (p->ns.load(std::memory_order_relaxed))
        for (int ch = 0; ch < c; ++ch)
            if (p->rn[ch])
                for (int off = 0; off + 480 <= n; off += 480)
                    rnnoise_process_frame(p->rn[ch], p->chf[ch] + off, p->chf[ch] + off);

    for (int ch = 0; ch < c; ++ch) {
        for (int i = 0; i < n; ++i)
            p->chi[ch][i] = static_cast<int16_t>(std::clamp(p->chf[ch][i], -32768.0f, 32767.0f));
        if (p->agcOn.load(std::memory_order_relaxed) && p->agc[ch])
            speex_preprocess_run(p->agc[ch], p->chi[ch]);
    }

    const float g = std::pow(10.0f, p->gainDb.load(std::memory_order_relaxed) / 20.0f);
    const bool mute = p->muted.load(std::memory_order_relaxed) != 0;
    for (int ch = 0; ch < c; ++ch)
        for (int i = 0; i < n; ++i) {
            float x = mute ? 0.0f : SoftLimit(static_cast<float>(p->chi[ch][i]) * g);
            p->ibuf[i * c + ch] = static_cast<int16_t>(std::clamp(x, -32768.0f, 32767.0f));
        }

    const uint32_t head = p->head.load(std::memory_order_relaxed);
    const uint32_t next = (head + 1) % kRingSlots;
    if (next == p->tail.load(std::memory_order_acquire)) {
        p->dropped.fetch_add(1, std::memory_order_relaxed);
    } else {
        PacketSlot& slot = p->ring[head];
        const uint64_t ts = p->baseTsUs + p->totalSamples * 1000000ULL / kSampleRate;
        int len = opus_encode(p->enc, p->ibuf, n, slot.data, kSlotSize);
        if (len > 0) {
            slot.seq = p->seq++;
            slot.ts = ts;
            slot.len = len;
            p->head.store(next, std::memory_order_release);
            p->packets.fetch_add(1, std::memory_order_relaxed);
        }
    }

    p->totalSamples += static_cast<uint64_t>(n);
    p->frames.fetch_add(1, std::memory_order_relaxed);
}

} // namespace

extern "C" {

void* rm_pipeline_create(const rm_config* cfg) {
    if (!cfg || cfg->sample_rate != kSampleRate)
        return nullptr;
    if (cfg->frame_ms != 10 && cfg->frame_ms != 20)
        return nullptr;
    const int channels = cfg->channels == 0 ? 1 : cfg->channels;
    if (channels != 1 && channels != 2)
        return nullptr;

    auto* p = new Pipeline();
    p->channels = channels;
    p->frameSamples = cfg->frame_ms * kSampleRate / 1000;
    p->gainDb.store(cfg->gain_db);
    p->muted.store(cfg->muted);
    p->ns.store(cfg->ns_enabled);
    p->agcOn.store(cfg->agc_enabled);
    p->baseTsUs = static_cast<uint64_t>(
        std::chrono::duration_cast<std::chrono::microseconds>(
            std::chrono::steady_clock::now().time_since_epoch()).count());

    int err = 0;
    p->enc = opus_encoder_create(kSampleRate, channels, OPUS_APPLICATION_VOIP, &err);
    if (!p->enc || err != OPUS_OK) {
        rm_pipeline_destroy(p);
        return nullptr;
    }
    opus_encoder_ctl(p->enc, OPUS_SET_BITRATE(cfg->bitrate > 0 ? cfg->bitrate : 32000));
    opus_encoder_ctl(p->enc, OPUS_SET_INBAND_FEC(cfg->fec_enabled ? 1 : 0));
    opus_encoder_ctl(p->enc, OPUS_SET_PACKET_LOSS_PERC(10));
    opus_encoder_ctl(p->enc, OPUS_SET_DTX(cfg->dtx_enabled ? 1 : 0));
    opus_encoder_ctl(p->enc, OPUS_SET_SIGNAL(OPUS_SIGNAL_VOICE));

    if (cfg->ns_enabled)
        for (int ch = 0; ch < channels; ++ch)
            p->rn[ch] = rnnoise_create(nullptr);

    if (cfg->agc_enabled) {
        for (int ch = 0; ch < channels; ++ch) {
            p->agc[ch] = speex_preprocess_state_init(p->frameSamples, kSampleRate);
            if (p->agc[ch]) {
                int off = 0, on = 1;
                float level = 8000.0f;
                speex_preprocess_ctl(p->agc[ch], SPEEX_PREPROCESS_SET_DENOISE, &off);
                speex_preprocess_ctl(p->agc[ch], SPEEX_PREPROCESS_SET_DEREVERB, &off);
                speex_preprocess_ctl(p->agc[ch], SPEEX_PREPROCESS_SET_VAD, &off);
                speex_preprocess_ctl(p->agc[ch], SPEEX_PREPROCESS_SET_AGC, &on);
                speex_preprocess_ctl(p->agc[ch], SPEEX_PREPROCESS_SET_AGC_LEVEL, &level);
            }
        }
    }
    return p;
}

void rm_pipeline_destroy(void* h) {
    auto* p = static_cast<Pipeline*>(h);
    if (!p) return;
    if (p->enc) opus_encoder_destroy(p->enc);
    for (auto* rn : p->rn) if (rn) rnnoise_destroy(rn);
    for (auto* a : p->agc) if (a) speex_preprocess_state_destroy(a);
    delete p;
}

int32_t rm_pipeline_feed(void* h, const int16_t* pcm, int32_t samples) {
    auto* p = static_cast<Pipeline*>(h);
    if (!p || !pcm || samples < 0) return -1;
    int32_t consumed = 0;
    const int frameValues = p->frameSamples * p->channels;
    while (consumed < samples) {
        int space = frameValues - p->accFill;
        int take = std::min(space, samples - consumed);
        std::memcpy(p->acc + p->accFill, pcm + consumed, take * sizeof(int16_t));
        p->accFill += take;
        consumed += take;
        if (p->accFill == frameValues) {
            ProcessFrame(p);
            p->accFill = 0;
        }
    }
    return consumed;
}

int32_t rm_pipeline_read_packet(void* h, uint8_t* buf, int32_t cap,
                                uint32_t* seq, uint64_t* capture_ts_us) {
    auto* p = static_cast<Pipeline*>(h);
    if (!p || !buf) return -1;
    const uint32_t tail = p->tail.load(std::memory_order_relaxed);
    if (tail == p->head.load(std::memory_order_acquire))
        return 0;
    const PacketSlot& slot = p->ring[tail];
    if (slot.len > cap) return -1;
    std::memcpy(buf, slot.data, slot.len);
    if (seq) *seq = slot.seq;
    if (capture_ts_us) *capture_ts_us = slot.ts;
    p->tail.store((tail + 1) % kRingSlots, std::memory_order_release);
    return slot.len;
}

void rm_pipeline_set_gain(void* h, float db) {
    if (auto* p = static_cast<Pipeline*>(h)) p->gainDb.store(db);
}
void rm_pipeline_set_mute(void* h, int32_t muted) {
    if (auto* p = static_cast<Pipeline*>(h)) p->muted.store(muted);
}
void rm_pipeline_set_ns(void* h, int32_t enabled) {
    if (auto* p = static_cast<Pipeline*>(h)) p->ns.store(enabled);
}
void rm_pipeline_set_agc(void* h, int32_t enabled) {
    if (auto* p = static_cast<Pipeline*>(h)) p->agcOn.store(enabled);
}
void rm_pipeline_set_bitrate(void* h, int32_t bps) {
    if (auto* p = static_cast<Pipeline*>(h))
        opus_encoder_ctl(p->enc, OPUS_SET_BITRATE(bps));
}
void rm_pipeline_set_dtx(void* h, int32_t enabled) {
    if (auto* p = static_cast<Pipeline*>(h))
        opus_encoder_ctl(p->enc, OPUS_SET_DTX(enabled ? 1 : 0));
}
void rm_pipeline_get_stats(void* h, rm_stats* out) {
    auto* p = static_cast<Pipeline*>(h);
    if (!p || !out) return;
    out->frames_processed = p->frames.load();
    out->packets_produced = p->packets.load();
    out->packets_dropped = p->dropped.load();
}

void rm_pipeline_flush(void* h) {
    auto* p = static_cast<Pipeline*>(h);
    if (!p) return;
    p->tail.store(p->head.load(std::memory_order_acquire), std::memory_order_release);
}

void* rm_decoder_create(int32_t sample_rate, int32_t channels) {
    int err = 0;
    OpusDecoder* d = opus_decoder_create(sample_rate, channels, &err);
    return err == OPUS_OK ? d : nullptr;
}

void rm_decoder_destroy(void* h) {
    if (h) opus_decoder_destroy(static_cast<OpusDecoder*>(h));
}

int32_t rm_decoder_decode(void* h, const uint8_t* data, int32_t len,
                          float* pcm, int32_t max_samples, int32_t fec) {
    if (!h || !pcm) return -1;
    return opus_decode_float(static_cast<OpusDecoder*>(h), data, len, pcm, max_samples, fec);
}

int32_t rm_decoder_plc(void* h, float* pcm, int32_t samples) {
    if (!h || !pcm) return -1;
    return opus_decode_float(static_cast<OpusDecoder*>(h), nullptr, 0, pcm, samples, 0);
}

} /* extern "C" */