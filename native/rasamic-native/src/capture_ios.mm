#include "rasamic_native.h"

#import <AVFoundation/AVFoundation.h>

#include <atomic>
#include <cstring>

namespace {

constexpr int kMaxTapFrames = 4800; /* 100 ms headroom over the 10 ms tap */

struct Capture {
    AVAudioEngine* engine = nil;
    rm_pcm_callback cb = nullptr;
    void* user = nullptr;
    int32_t channels = 1;
    int16_t scratch[kMaxTapFrames];
    id interruptionObserver = nil;
    id routeObserver = nil;
    std::atomic<int> running{0};
};

void ConvertAndFeed(Capture* c, AVAudioPCMBuffer* buffer) {
    const AVAudioFrameCount frames = buffer.frameLength;
    if (frames == 0 || frames > kMaxTapFrames)
        return;
    const float* const* src = buffer.floatChannelData;
    if (!src)
        return;
    /* Tap format is mono float32 at the requested rate; take channel 0. */
    const float* in = src[0];
    for (AVAudioFrameCount i = 0; i < frames; ++i) {
        float x = in[i] * 32768.0f;
        if (x > 32767.0f) x = 32767.0f;
        if (x < -32768.0f) x = -32768.0f;
        c->scratch[i] = static_cast<int16_t>(x);
    }
    c->cb(c->user, c->scratch, static_cast<int32_t>(frames));
}

void InstallTap(Capture* c) {
    AVAudioInputNode* input = c->engine.inputNode;
    /* Requesting 48 kHz mono float makes the engine resample/remap from the
     * hardware format inside the tap — the DSP chain stays hard-locked to 48k. */
    AVAudioFormat* tapFormat = [[AVAudioFormat alloc] initWithCommonFormat:AVAudioPCMFormatFloat32
                                                               sampleRate:48000
                                                                 channels:1
                                                              interleaved:NO];
    [input installTapOnBus:0 bufferSize:480 format:tapFormat block:^(AVAudioPCMBuffer* buffer, AVAudioTime*) {
        ConvertAndFeed(c, buffer);
    }];
}

} // namespace

extern "C" {

void* rm_capture_create(int32_t sample_rate, int32_t channels, rm_pcm_callback cb, void* user) {
    if (!cb || sample_rate != 48000 || channels != 1)
        return nullptr;

    AVAudioSession* session = [AVAudioSession sharedInstance];
    NSError* err = nil;
    /* Measurement mode disables Apple's AGC/noise processing so ours is authoritative. */
    if (![session setCategory:AVAudioSessionCategoryPlayAndRecord
                         mode:AVAudioSessionModeMeasurement
                      options:0
                        error:&err])
        return nullptr;
    [session setPreferredSampleRate:48000 error:nil];
    [session setPreferredIOBufferDuration:0.005 error:nil];
    if (![session setActive:YES error:&err])
        return nullptr;

    auto* c = new Capture();
    c->cb = cb;
    c->user = user;
    c->channels = channels;
    c->engine = [[AVAudioEngine alloc] init];
    InstallTap(c);

    NSNotificationCenter* nc = [NSNotificationCenter defaultCenter];
    c->interruptionObserver = [nc addObserverForName:AVAudioSessionInterruptionNotification
                                              object:session
                                               queue:nil
                                          usingBlock:^(NSNotification* note) {
        const auto type = static_cast<AVAudioSessionInterruptionType>(
            [note.userInfo[AVAudioSessionInterruptionTypeKey] unsignedIntegerValue]);
        if (type == AVAudioSessionInterruptionTypeEnded && c->running.load()) {
            [[AVAudioSession sharedInstance] setActive:YES error:nil];
            [c->engine startAndReturnError:nil];
        }
    }];
    c->routeObserver = [nc addObserverForName:AVAudioSessionRouteChangeNotification
                                       object:session
                                        queue:nil
                                   usingBlock:^(NSNotification* note) {
        const auto reason = static_cast<AVAudioSessionRouteChangeReason>(
            [note.userInfo[AVAudioSessionRouteChangeReasonKey] unsignedIntegerValue]);
        if (reason == AVAudioSessionRouteChangeReasonNewDeviceAvailable && c->running.load()) {
            /* New input route: reinstall the tap against the new hardware format. */
            [c->engine stop];
            [c->engine.inputNode removeTapOnBus:0];
            InstallTap(c);
            [c->engine startAndReturnError:nil];
        }
    }];
    return c;
}

int32_t rm_capture_start(void* h) {
    auto* c = static_cast<Capture*>(h);
    if (!c) return -1;
    NSError* err = nil;
    if (![c->engine startAndReturnError:&err])
        return -1;
    c->running.store(1);
    return 0;
}

void rm_capture_stop(void* h) {
    auto* c = static_cast<Capture*>(h);
    if (!c) return;
    c->running.store(0);
    [c->engine stop];
}

void rm_capture_destroy(void* h) {
    auto* c = static_cast<Capture*>(h);
    if (!c) return;
    c->running.store(0);
    NSNotificationCenter* nc = [NSNotificationCenter defaultCenter];
    if (c->interruptionObserver) [nc removeObserver:c->interruptionObserver];
    if (c->routeObserver) [nc removeObserver:c->routeObserver];
    [c->engine.inputNode removeTapOnBus:0];
    [c->engine stop];
    [[AVAudioSession sharedInstance] setActive:NO error:nil];
    delete c;
}

/* 0 omni, 1 subcardioid, 2 cardioid, 3 hypercardioid (AVAudioSessionPolarPattern).
 * Returns 0 on success, -1 when the current input has no polar patterns. */
int32_t rm_capture_set_polar(void* h, int32_t pattern) {
    auto* c = static_cast<Capture*>(h);
    if (!c) return -1;
    AVAudioSession* session = [AVAudioSession sharedInstance];
    NSArray<AVAudioSessionPortDescription*>* inputs = session.currentRoute.inputs;
    if (inputs.count == 0)
        return -1;
    AVAudioSessionPortDescription* port = inputs[0];
    AVAudioSessionDataSourceDescription* source = port.selectedDataSource;
    if (!source)
        return -1;
    AVAudioSessionPolarPattern polar = nil;
    switch (pattern) {
        case 0: polar = AVAudioSessionPolarPatternOmnidirectional; break;
        case 1: polar = AVAudioSessionPolarPatternSubcardioid; break;
        case 2: polar = AVAudioSessionPolarPatternCardioid; break;
        case 3: polar = AVAudioSessionPolarPatternHypercardioid; break;
        default: return -1;
    }
    if (![source.supportedPolarPatterns containsObject:polar])
        return -1;
    NSError* err = nil;
    if (![source setPreferredPolarPattern:polar error:&err])
        return -1;
    return [session setPreferredInput:port error:&err] ? 0 : -1;
}

} /* extern "C" */