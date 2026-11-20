/* RasaMic native audio pipeline — C ABI.
 * Capture backends (Oboe/AVAudioEngine) feed PCM; DSP + Opus encode run here;
 * managed code only drains encoded packets. Single producer / single consumer. */
#ifndef RASAMIC_NATIVE_H
#define RASAMIC_NATIVE_H

#include <stdint.h>

#if defined(_WIN32)
#define RM_API __declspec(dllexport)
#else
#define RM_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

typedef struct rm_config {
    int32_t sample_rate;   /* must be 48000 in v1 */
    int32_t frame_ms;      /* 10 or 20 */
    int32_t bitrate;       /* Opus bitrate, bps (16000..96000 typical) */
    int32_t ns_enabled;    /* RNNoise */
    int32_t agc_enabled;   /* SpeexDSP AGC */
    float   gain_db;       /* user gain applied after AGC */
    int32_t muted;
    int32_t fec_enabled;   /* Opus in-band FEC */
    int32_t dtx_enabled;
    int32_t channels;      /* 1 (mono) or 2 (stereo, interleaved); 0 = mono */
} rm_config;

typedef struct rm_stats {
    uint64_t frames_processed;
    uint64_t packets_produced;
    uint64_t packets_dropped;
} rm_stats;

RM_API void* rm_pipeline_create(const rm_config* cfg);
RM_API void  rm_pipeline_destroy(void* h);

/* Feed interleaved int16 PCM at cfg.sample_rate (mono, or L/R per cfg.channels).
 * `samples` counts int16 values (frames * channels). Buffers into frames,
 * runs DSP + Opus, pushes packets to the internal ring. Returns values consumed. */
RM_API int32_t rm_pipeline_feed(void* h, const int16_t* pcm, int32_t samples);

/* Pop one encoded Opus packet. Returns payload length (0 = ring empty).
 * seq/capture_ts_us identify the packet for the wire header. */
RM_API int32_t rm_pipeline_read_packet(void* h, uint8_t* buf, int32_t cap,
                                       uint32_t* seq, uint64_t* capture_ts_us);

RM_API void rm_pipeline_set_gain(void* h, float db);
RM_API void rm_pipeline_set_mute(void* h, int32_t muted);
RM_API void rm_pipeline_set_ns(void* h, int32_t enabled);
RM_API void rm_pipeline_set_agc(void* h, int32_t enabled);
RM_API void rm_pipeline_set_bitrate(void* h, int32_t bps);
RM_API void rm_pipeline_set_dtx(void* h, int32_t enabled);
RM_API void rm_pipeline_get_stats(void* h, rm_stats* out);

/* Server-side Opus decode helpers (float output). */
RM_API void*   rm_decoder_create(int32_t sample_rate, int32_t channels);
RM_API void    rm_decoder_destroy(void* h);
/* Returns samples per channel decoded, or negative Opus error.
 * fec=1 decodes the LBRR (previous lost) frame from this packet's payload. */
RM_API int32_t rm_decoder_decode(void* h, const uint8_t* data, int32_t len,
                                 float* pcm, int32_t max_samples, int32_t fec);
RM_API int32_t rm_decoder_plc(void* h, float* pcm, int32_t samples);

/* Monitor output (server speaker playback via miniaudio) — driverless demo mode. */
RM_API void*   rm_monitor_create(int32_t sample_rate, int32_t channels);
/* Opens the first playback device whose name contains name_filter (e.g. a
 * PipeWire loopback sink). Returns NULL when no device matches. */
RM_API void*   rm_monitor_create_named(int32_t sample_rate, int32_t channels, const char* name_filter);
RM_API int32_t rm_monitor_write(void* h, const float* pcm, int32_t samples);
RM_API void    rm_monitor_destroy(void* h);

/* Microphone capture. Callback runs on the native RT thread; keep it lock-free. */
typedef void (*rm_pcm_callback)(void* user, const int16_t* pcm, int32_t samples);
RM_API void*   rm_capture_create(int32_t sample_rate, int32_t channels, rm_pcm_callback cb, void* user);
RM_API int32_t rm_capture_start(void* h);
RM_API void    rm_capture_stop(void* h);
RM_API void    rm_capture_destroy(void* h);
/* iOS only: 0 omni / 1 subcardioid / 2 cardioid / 3 hypercardioid.
 * Returns -1 on other platforms or when the input has no polar patterns. */
RM_API int32_t rm_capture_set_polar(void* h, int32_t pattern);

#ifdef __cplusplus
}
#endif

#endif /* RASAMIC_NATIVE_H */