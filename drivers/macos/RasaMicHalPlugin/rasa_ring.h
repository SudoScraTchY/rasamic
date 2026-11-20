/* Shared SPSC ring layout: macOS HAL plugin (reader) <-> rasamicd (writer).
 * File = 4096-byte header page + capacity_frames * channels float32 samples.
 * Positions are monotonic frame counters; index = pos % capacity_frames.
 * Writer owns write_pos, reader owns read_pos; 64-bit aligned accesses are
 * atomic on x64/arm64. Data is published with a release barrier before
 * write_pos advances; the reader acquires write_pos before reading data.
 */
#ifndef RASA_RING_H
#define RASA_RING_H

#include <stdint.h>

#define RASA_RING_MAGIC 0x41534152u /* "RASA" little-endian */
#define RASA_RING_VERSION 1u
#define RASA_RING_HEADER_SIZE 4096u

#define RASA_RING_OFF_MAGIC 0
#define RASA_RING_OFF_VERSION 4
#define RASA_RING_OFF_SAMPLE_RATE 8
#define RASA_RING_OFF_CHANNELS 12
#define RASA_RING_OFF_CAPACITY 16
#define RASA_RING_OFF_WRITE_POS 24
#define RASA_RING_OFF_READ_POS 32
#define RASA_RING_OFF_STARTED 40

#ifdef __cplusplus
extern "C" {
#endif

typedef struct RasaRingHeader {
    uint32_t magic;
    uint32_t version;
    uint32_t sample_rate;
    uint32_t channels;
    uint64_t capacity_frames;
    uint64_t write_pos;
    uint64_t read_pos;
    uint32_t started;
    uint32_t reserved_;
} RasaRingHeader;

#ifdef __cplusplus
}
#endif

#endif /* RASA_RING_H */