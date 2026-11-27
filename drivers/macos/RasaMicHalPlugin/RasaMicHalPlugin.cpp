// Rasa macOS virtual microphone — AudioServerPlugIn for coreaudiod.
// MIT. Exposes one input device "Rasa Mic" (48 kHz mono float32) whose samples
// come from the file-backed SPSC ring written by rasamicd --hal
// (src/RasaMic.Server/Backends/MacHalBackend.cs, layout in rasa_ring.h).
//
// Sandbox note: coreaudiod may deny file access outside the plugin bundle.
// The CI spike (.github/workflows/ci.yml, macos-hal-spike) probes whether the
// ring at /Library/Application Support/RasaMic/ring.bin is readable; if denied,
// the fallback is a Mach service (see plan M5).

#include <CoreAudio/AudioServerPlugIn.h>
#include <CoreFoundation/CoreFoundation.h>
#include <CoreFoundation/CFPlugInCOM.h>
#include <mach/mach_time.h>

#include <atomic>
#include <cstdio>
#include <cstring>
#include <fcntl.h>
#include <sys/mman.h>
#include <sys/stat.h>
#include <unistd.h>

#include "rasa_ring.h"

namespace {

constexpr AudioObjectID kPluginObjectID = kAudioObjectPlugInObject; // 1
constexpr AudioObjectID kDeviceObjectID = 2;
constexpr AudioObjectID kInputStreamObjectID = 3;
constexpr AudioObjectID kBoxObjectID = 4;

constexpr Float64 kSampleRate = 48000.0;
constexpr UInt32 kChannels = 1;
constexpr UInt32 kBufferFrameSize = 512;
constexpr UInt32 kZeroTimeStampPeriod = kBufferFrameSize;
// Current plug-in SDKs do not expose these legacy device selectors.
constexpr AudioObjectPropertySelector kRasaBufferFrameSize = 'fsiz';
constexpr AudioObjectPropertySelector kRasaStreamConfiguration = 'slay';

constexpr const char* kRingPath = "/Library/Application Support/RasaMic/ring.bin";

AudioStreamBasicDescription NativeFormat()
{
    AudioStreamBasicDescription f{};
    f.mSampleRate = kSampleRate;
    f.mFormatID = kAudioFormatLinearPCM;
    f.mFormatFlags = kAudioFormatFlagIsFloat | kAudioFormatFlagIsPacked | kAudioFormatFlagsNativeEndian;
    f.mBytesPerPacket = 4;
    f.mFramesPerPacket = 1;
    f.mBytesPerFrame = 4;
    f.mChannelsPerFrame = kChannels;
    f.mBitsPerChannel = 32;
    return f;
}

// Reader side of the shared ring. Mirrors MmapRingReader.Read: underrun ->
// silence, writer lapped us -> drop oldest. read_pos is ours to publish, so
// the mapping must be writable (O_RDWR).
class RingReader
{
public:
    ~RingReader() { Close(); }

    bool IsOpen() const { return base_ != nullptr; }

    bool Open()
    {
        Close();
        int fd = open(kRingPath, O_RDWR);
        if (fd < 0)
            return false;
        struct stat st{};
        if (fstat(fd, &st) != 0 || st.st_size < RASA_RING_HEADER_SIZE)
        {
            close(fd);
            return false;
        }
        void* base = mmap(nullptr, static_cast<size_t>(st.st_size), PROT_READ | PROT_WRITE, MAP_SHARED, fd, 0);
        if (base == MAP_FAILED)
        {
            close(fd);
            return false;
        }
        auto* h = static_cast<uint8_t*>(base);
        uint32_t magic, version, channels;
        uint64_t capacity;
        memcpy(&magic, h + RASA_RING_OFF_MAGIC, 4);
        memcpy(&version, h + RASA_RING_OFF_VERSION, 4);
        memcpy(&channels, h + RASA_RING_OFF_CHANNELS, 4);
        memcpy(&capacity, h + RASA_RING_OFF_CAPACITY, 8);
        if (magic != RASA_RING_MAGIC || version != RASA_RING_VERSION ||
            capacity == 0 || (capacity & (capacity - 1)) != 0 ||
            channels == 0 || channels > 2 ||
            static_cast<uint64_t>(st.st_size) < RASA_RING_HEADER_SIZE + capacity * channels * 4)
        {
            munmap(base, static_cast<size_t>(st.st_size));
            close(fd);
            return false;
        }
        fd_ = fd;
        base_ = h;
        length_ = static_cast<size_t>(st.st_size);
        capacity_ = capacity;
        channels_ = channels;
        memcpy(&readPos_, h + RASA_RING_OFF_READ_POS, 8);
        return true;
    }

    void Close()
    {
        if (base_ != nullptr)
        {
            munmap(base_, length_);
            base_ = nullptr;
        }
        if (fd_ >= 0)
        {
            close(fd_);
            fd_ = -1;
        }
    }

    // Fills dest with frames mono samples; silence when the ring is absent or
    // underruns. Returns frames actually read.
    uint32_t Read(float* dest, uint32_t frames)
    {
        if (base_ == nullptr)
        {
            memset(dest, 0, frames * sizeof(float));
            return 0;
        }
        uint64_t writePos;
        memcpy(&writePos, base_ + RASA_RING_OFF_WRITE_POS, 8);
        std::atomic_thread_fence(std::memory_order_acquire);
        if (writePos < readPos_) // server restarted the ring
            readPos_ = writePos;
        uint64_t available = writePos - readPos_;
        if (available == 0)
        {
            memset(dest, 0, frames * sizeof(float));
            return 0;
        }
        if (available > capacity_)
            readPos_ = writePos - capacity_;
        uint32_t n = static_cast<uint32_t>(available < frames ? available : frames);
        const auto* data = reinterpret_cast<const float*>(base_ + RASA_RING_HEADER_SIZE);
        uint64_t index = (readPos_ % capacity_) * channels_;
        uint64_t total = static_cast<uint64_t>(n) * channels_;
        uint64_t first = capacity_ * channels_ - index;
        if (first > total)
            first = total;
        for (uint64_t i = 0; i < first; i += channels_)
            dest[i / channels_] = data[index + i];
        for (uint64_t i = first; i < total; i += channels_)
            dest[i / channels_] = data[i - first];
        readPos_ += n;
        std::atomic_thread_fence(std::memory_order_release);
        memcpy(base_ + RASA_RING_OFF_READ_POS, &readPos_, 8);
        if (n < frames)
            memset(dest + n, 0, (frames - n) * sizeof(float));
        return n;
    }

private:
    int fd_ = -1;
    uint8_t* base_ = nullptr;
    size_t length_ = 0;
    uint64_t capacity_ = 0;
    uint32_t channels_ = 1;
    uint64_t readPos_ = 0;
};

struct RasaDriver
{
    // COM: the instance pointer must point at a slot holding the interface.
    const AudioServerPlugInDriverInterface* interface;
    std::atomic<ULONG> refCount{1};
    AudioServerPlugInHostRef host = nullptr;
    std::atomic<UInt32> ioClients{0};
    uint64_t anchorHostTime = 0;
    uint64_t timestampCount = 0;
    uint64_t cyclesSinceOpenTry = 0;
    RingReader ring;
};

RasaDriver* Self(AudioServerPlugInDriverRef ref)
{
    return reinterpret_cast<RasaDriver*>(ref);
}

HRESULT STDMETHODCALLTYPE QueryInterface(void* self, REFIID uuid, LPVOID* outInterface)
{
    if (outInterface == nullptr)
        return E_POINTER;
    *outInterface = nullptr;
    const CFUUIDBytes driver = CFUUIDGetUUIDBytes(kAudioServerPlugInDriverInterfaceUUID);
    const CFUUIDBytes unknown = {
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0xC0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x46
    };
    if (memcmp(&uuid, &driver, sizeof(uuid)) == 0 || memcmp(&uuid, &unknown, sizeof(uuid)) == 0)
    {
        *outInterface = self;
        static_cast<RasaDriver*>(self)->refCount.fetch_add(1);
        return S_OK;
    }
    return E_NOINTERFACE;
}

ULONG STDMETHODCALLTYPE AddRef(void* self)
{
    return static_cast<RasaDriver*>(self)->refCount.fetch_add(1) + 1;
}

ULONG STDMETHODCALLTYPE Release(void* self)
{
    auto* d = static_cast<RasaDriver*>(self);
    ULONG remaining = d->refCount.fetch_sub(1) - 1;
    if (remaining == 0)
        delete d;
    return remaining;
}

OSStatus STDMETHODCALLTYPE Initialize(AudioServerPlugInDriverRef ref, AudioServerPlugInHostRef host)
{
    Self(ref)->host = host;
    return noErr;
}

OSStatus STDMETHODCALLTYPE CreateDevice(AudioServerPlugInDriverRef, CFDictionaryRef, const AudioServerPlugInClientInfo*, AudioObjectID*)
{
    return kAudioHardwareUnsupportedOperationError;
}

OSStatus STDMETHODCALLTYPE DestroyDevice(AudioServerPlugInDriverRef, AudioObjectID)
{
    return kAudioHardwareUnsupportedOperationError;
}

OSStatus STDMETHODCALLTYPE AddDeviceClient(AudioServerPlugInDriverRef, AudioObjectID, const AudioServerPlugInClientInfo*)
{
    return noErr;
}

OSStatus STDMETHODCALLTYPE RemoveDeviceClient(AudioServerPlugInDriverRef, AudioObjectID, const AudioServerPlugInClientInfo*)
{
    return noErr;
}

OSStatus STDMETHODCALLTYPE PerformDeviceConfigurationChange(AudioServerPlugInDriverRef, AudioObjectID, UInt64, void*)
{
    return noErr;
}

OSStatus STDMETHODCALLTYPE AbortDeviceConfigurationChange(AudioServerPlugInDriverRef, AudioObjectID, UInt64, void*)
{
    return noErr;
}

bool HasDeviceProperty(AudioObjectID object, const AudioObjectPropertyAddress& a)
{
    switch (a.mSelector)
    {
        case kAudioObjectPropertyBaseClass:
        case kAudioObjectPropertyClass:
        case kAudioObjectPropertyOwner:
        case kAudioObjectPropertyName:
        case kAudioObjectPropertyManufacturer:
        case kAudioDevicePropertyDeviceUID:
        case kAudioDevicePropertyModelUID:
        case kAudioDevicePropertyTransportType:
        case kAudioDevicePropertyDeviceIsAlive:
        case kAudioDevicePropertyDeviceIsRunning:
        case kAudioDevicePropertyDeviceIsHidden:
        case kAudioDevicePropertyNominalSampleRate:
        case kAudioDevicePropertyAvailableNominalSampleRates:
        case kAudioDevicePropertyRelatedDevices:
        case kAudioDevicePropertyClockDomain:
        case kAudioObjectPropertyControlList:
        case kRasaBufferFrameSize:
        case kAudioDevicePropertyZeroTimeStampPeriod:
        case kAudioDevicePropertyClockAlgorithm:
        case kAudioDevicePropertyClockIsStable:
            return true;
        case kAudioDevicePropertyDeviceCanBeDefaultDevice:
        case kAudioDevicePropertyDeviceCanBeDefaultSystemDevice:
            return a.mScope == kAudioObjectPropertyScopeGlobal || a.mScope == kAudioObjectPropertyScopeInput;
        case kAudioDevicePropertyStreams:
        case kRasaStreamConfiguration:
            return true;
        case kAudioDevicePropertyLatency:
        case kAudioDevicePropertySafetyOffset:
            return a.mScope != kAudioObjectPropertyScopeOutput;
        default:
            return false;
    }
}

bool HasStreamProperty(const AudioObjectPropertyAddress& a)
{
    switch (a.mSelector)
    {
        case kAudioObjectPropertyBaseClass:
        case kAudioObjectPropertyClass:
        case kAudioObjectPropertyOwner:
        case kAudioStreamPropertyIsActive:
        case kAudioStreamPropertyDirection:
        case kAudioStreamPropertyTerminalType:
        case kAudioStreamPropertyStartingChannel:
        case kAudioStreamPropertyLatency:
        case kAudioStreamPropertyPhysicalFormat:
        case kAudioStreamPropertyVirtualFormat:
        case kAudioStreamPropertyAvailablePhysicalFormats:
        case kAudioStreamPropertyAvailableVirtualFormats:
            return true;
        default:
            return false;
    }
}

bool HasBoxProperty(const AudioObjectPropertyAddress& a)
{
    switch (a.mSelector)
    {
        case kAudioObjectPropertyBaseClass:
        case kAudioObjectPropertyClass:
        case kAudioObjectPropertyOwner:
        case kAudioBoxPropertyAcquired:
        case kAudioBoxPropertyDeviceList:
            return true;
        default:
            return false;
    }
}

UInt32 OwnedObjectsSize(AudioObjectID object, AudioObjectPropertyScope scope)
{
    if (object == kPluginObjectID)
        return sizeof(AudioObjectID);
    if (object == kDeviceObjectID &&
        (scope == kAudioObjectPropertyScopeGlobal || scope == kAudioObjectPropertyScopeInput))
        return sizeof(AudioObjectID);
    return 0;
}

bool HasCommonProperty(AudioObjectPropertySelector selector)
{
    return selector == kAudioObjectPropertyOwnedObjects ||
           selector == kAudioObjectPropertyCustomPropertyInfoList ||
           selector == kAudioObjectPropertyIdentify;
}

void LogUnknownProperty(AudioObjectID object, const AudioObjectPropertyAddress& address)
{
    const auto s = address.mSelector;
    const char fourCC[5] = {
        static_cast<char>((s >> 24) & 0xff), static_cast<char>((s >> 16) & 0xff),
        static_cast<char>((s >> 8) & 0xff), static_cast<char>(s & 0xff), '\0'};
    fprintf(stderr, "Rasa Mic HAL unknown property object=%u selector=0x%08x (%s) scope=%u element=%u\n",
            object, s, fourCC, address.mScope, address.mElement);
}

Boolean STDMETHODCALLTYPE HasProperty(AudioServerPlugInDriverRef, AudioObjectID object, pid_t, const AudioObjectPropertyAddress* address)
{
    if (address == nullptr)
        return false;
    if (HasCommonProperty(address->mSelector))
        return true;
    switch (object)
    {
        case kPluginObjectID:
            switch (address->mSelector)
            {
                case kAudioObjectPropertyBaseClass:
                case kAudioObjectPropertyClass:
                case kAudioObjectPropertyOwner:
                case kAudioObjectPropertyName:
                case kAudioObjectPropertyManufacturer:
                case kAudioPlugInPropertyDeviceList:
                case kAudioPlugInPropertyBoxList:
                case kAudioPlugInPropertyTranslateUIDToDevice:
                    return true;
                default:
                    break;
            }
            LogUnknownProperty(object, *address);
            return false;
        case kDeviceObjectID:
            if (HasDeviceProperty(object, *address))
                return true;
            break;
        case kInputStreamObjectID:
            if (HasStreamProperty(*address))
                return true;
            break;
        case kBoxObjectID:
            if (HasBoxProperty(*address))
                return true;
            break;
        default:
            break;
    }
    LogUnknownProperty(object, *address);
    return false;
    }
}

OSStatus STDMETHODCALLTYPE IsPropertySettable(AudioServerPlugInDriverRef, AudioObjectID, pid_t, const AudioObjectPropertyAddress*, Boolean* outSettable)
{
    if (outSettable == nullptr)
        return kAudioHardwareBadPropertySizeError;
    *outSettable = false;
    return noErr;
}

UInt32 StreamConfigurationSize(AudioObjectID object, const AudioObjectPropertyAddress& a)
{
    // One mono buffer for global/input scopes; empty AudioBufferList elsewhere.
    bool hasStream = object == kDeviceObjectID && a.mScope != kAudioObjectPropertyScopeOutput;
    return static_cast<UInt32>(offsetof(AudioBufferList, mBuffers) + (hasStream ? sizeof(AudioBuffer) : 0));
}

OSStatus STDMETHODCALLTYPE GetPropertyDataSize(AudioServerPlugInDriverRef ref, AudioObjectID object, pid_t pid, const AudioObjectPropertyAddress* address, UInt32 qualifierSize, const void* qualifier, UInt32* outSize)
{
    if (address == nullptr || outSize == nullptr)
        return kAudioHardwareBadPropertySizeError;
    if (!HasProperty(ref, object, pid, address))
        return kAudioHardwareUnknownPropertyError;
    if (address->mSelector == kAudioObjectPropertyOwnedObjects)
    {
        *outSize = OwnedObjectsSize(object, address->mScope);
        return noErr;
    }
    if (address->mSelector == kAudioObjectPropertyCustomPropertyInfoList ||
        address->mSelector == kAudioObjectPropertyControlList)
    {
        *outSize = 0;
        return noErr;
    }
    switch (address->mSelector)
    {
        case kAudioObjectPropertyBaseClass:
        case kAudioObjectPropertyClass:
        case kAudioObjectPropertyOwner:
        case kAudioDevicePropertyTransportType:
        case kAudioDevicePropertyDeviceIsAlive:
        case kAudioDevicePropertyDeviceIsRunning:
        case kAudioDevicePropertyDeviceIsHidden:
        case kAudioDevicePropertyDeviceCanBeDefaultDevice:
        case kAudioDevicePropertyDeviceCanBeDefaultSystemDevice:
        case kAudioDevicePropertyRelatedDevices:
        case kAudioDevicePropertyClockDomain:
        case kAudioObjectPropertyIdentify:
        case kAudioPlugInPropertyTranslateUIDToDevice:
        case kRasaBufferFrameSize:
        case kAudioDevicePropertyZeroTimeStampPeriod:
        case kAudioDevicePropertyClockAlgorithm:
        case kAudioDevicePropertyClockIsStable:
        case kAudioDevicePropertyLatency:
        case kAudioDevicePropertySafetyOffset:
        case kAudioStreamPropertyIsActive:
        case kAudioStreamPropertyDirection:
        case kAudioStreamPropertyTerminalType:
        case kAudioStreamPropertyStartingChannel:
        case kAudioStreamPropertyLatency:
            *outSize = sizeof(UInt32);
            return noErr;
        case kAudioObjectPropertyName:
        case kAudioObjectPropertyManufacturer:
        case kAudioDevicePropertyDeviceUID:
        case kAudioDevicePropertyModelUID:
            *outSize = sizeof(CFStringRef);
            return noErr;
        case kAudioDevicePropertyNominalSampleRate:
            *outSize = sizeof(Float64);
            return noErr;
        case kAudioDevicePropertyAvailableNominalSampleRates:
            *outSize = sizeof(AudioValueRange);
            return noErr;
        case kAudioPlugInPropertyDeviceList:
        case kAudioPlugInPropertyBoxList:
        case kAudioBoxPropertyDeviceList:
            *outSize = sizeof(AudioObjectID);
            return noErr;
        case kAudioBoxPropertyAcquired:
            *outSize = sizeof(UInt32);
            return noErr;
        case kAudioObjectPropertyCustomPropertyInfoList:
            *outSize = 0;
            return noErr;
        case kAudioDevicePropertyStreams:
            *outSize = address->mScope == kAudioObjectPropertyScopeInput ? sizeof(AudioObjectID) : 0;
            return noErr;
        case kRasaStreamConfiguration:
            *outSize = StreamConfigurationSize(object, *address);
            return noErr;
        case kAudioStreamPropertyPhysicalFormat:
        case kAudioStreamPropertyVirtualFormat:
            *outSize = sizeof(AudioStreamBasicDescription);
            return noErr;
        case kAudioStreamPropertyAvailablePhysicalFormats:
        case kAudioStreamPropertyAvailableVirtualFormats:
            *outSize = sizeof(AudioStreamRangedDescription);
            return noErr;
        default:
            LogUnknownProperty(object, *address);
            return kAudioHardwareUnknownPropertyError;
    }
}

OSStatus CopyCFString(CFStringRef value, UInt32 inDataSize, UInt32* outSize, void* outData)
{
    if (outSize == nullptr || outData == nullptr || inDataSize < sizeof(CFStringRef))
        return kAudioHardwareBadPropertySizeError;
    *static_cast<CFStringRef*>(outData) = static_cast<CFStringRef>(CFRetain(value));
    *outSize = sizeof(CFStringRef);
    return noErr;
}

OSStatus CopyUInt32(UInt32 value, UInt32 inDataSize, UInt32* outSize, void* outData)
{
    if (outSize == nullptr || outData == nullptr || inDataSize < sizeof(UInt32))
        return kAudioHardwareBadPropertySizeError;
    *static_cast<UInt32*>(outData) = value;
    *outSize = sizeof(UInt32);
    return noErr;
}

OSStatus CopyFloat64(Float64 value, UInt32 inDataSize, UInt32* outSize, void* outData)
{
    if (outSize == nullptr || outData == nullptr || inDataSize < sizeof(Float64))
        return kAudioHardwareBadPropertySizeError;
    *static_cast<Float64*>(outData) = value;
    *outSize = sizeof(Float64);
    return noErr;
}

OSStatus CopyObjectIDs(const AudioObjectID* values, UInt32 count, UInt32 inDataSize, UInt32* outSize, void* outData)
{
    if (outSize == nullptr)
        return kAudioHardwareBadPropertySizeError;
    const UInt32 bytes = count * sizeof(AudioObjectID);
    if (count != 0 && (outData == nullptr || inDataSize < bytes))
        return kAudioHardwareBadPropertySizeError;
    if (count != 0)
        memcpy(outData, values, bytes);
    *outSize = bytes;
    return noErr;
}

OSStatus STDMETHODCALLTYPE GetPropertyData(AudioServerPlugInDriverRef ref, AudioObjectID object, pid_t pid, const AudioObjectPropertyAddress* address, UInt32 qualifierSize, const void* qualifier, UInt32 inDataSize, UInt32* outSize, void* outData)
{
    if (address == nullptr)
        return kAudioHardwareBadPropertySizeError;
    if (!HasProperty(ref, object, pid, address))
        return kAudioHardwareUnknownPropertyError;
    auto* d = Self(ref);
    if (address->mSelector == kAudioObjectPropertyOwnedObjects)
    {
        AudioObjectID owned = object == kPluginObjectID ? kDeviceObjectID : kInputStreamObjectID;
        const bool hasOwned = object == kPluginObjectID ||
            (object == kDeviceObjectID && address->mScope != kAudioObjectPropertyScopeOutput);
        return CopyObjectIDs(hasOwned ? &owned : nullptr, hasOwned ? 1 : 0,
                             inDataSize, outSize, outData);
    }
    if (address->mSelector == kAudioObjectPropertyCustomPropertyInfoList ||
        address->mSelector == kAudioObjectPropertyControlList)
    {
        if (outSize == nullptr)
            return kAudioHardwareBadPropertySizeError;
        *outSize = 0;
        return noErr;
    }
    if (address->mSelector == kAudioObjectPropertyIdentify)
        return CopyUInt32(0, inDataSize, outSize, outData);
    switch (address->mSelector)
    {
        case kAudioObjectPropertyBaseClass:
            return CopyUInt32(kAudioObjectClassID, inDataSize, outSize, outData);
        case kAudioObjectPropertyClass:
            return CopyUInt32(object == kPluginObjectID ? kAudioPlugInClassID
                              : object == kDeviceObjectID ? kAudioDeviceClassID
                              : object == kBoxObjectID ? kAudioBoxClassID
                                                      : kAudioStreamClassID,
                              inDataSize, outSize, outData);
        case kAudioObjectPropertyOwner:
            return CopyUInt32(object == kPluginObjectID ? kAudioObjectUnknown
                              : object == kDeviceObjectID || object == kBoxObjectID ? kPluginObjectID
                                                                                  : kDeviceObjectID,
                              inDataSize, outSize, outData);
        case kAudioObjectPropertyName:
            return CopyCFString(CFSTR("Rasa Mic"), inDataSize, outSize, outData);
        case kAudioObjectPropertyManufacturer:
            return CopyCFString(CFSTR("Rasa"), inDataSize, outSize, outData);
        case kAudioDevicePropertyDeviceUID:
            return CopyCFString(CFSTR("rasamic-mic"), inDataSize, outSize, outData);
        case kAudioDevicePropertyModelUID:
            return CopyCFString(CFSTR("dev.rasamic.mic"), inDataSize, outSize, outData);
        case kAudioDevicePropertyTransportType:
            return CopyUInt32(kAudioDeviceTransportTypeVirtual, inDataSize, outSize, outData);
        case kAudioDevicePropertyDeviceIsAlive:
        case kAudioDevicePropertyDeviceCanBeDefaultDevice:
        case kAudioDevicePropertyDeviceCanBeDefaultSystemDevice:
        case kAudioDevicePropertyClockIsStable:
            return CopyUInt32(1, inDataSize, outSize, outData);
        case kAudioDevicePropertyDeviceIsRunning:
            return CopyUInt32(d->ioClients.load() > 0 ? 1 : 0, inDataSize, outSize, outData);
        case kAudioDevicePropertyDeviceIsHidden:
        case kAudioDevicePropertyClockDomain:
            return CopyUInt32(0, inDataSize, outSize, outData);
        case kAudioDevicePropertyRelatedDevices:
            return CopyObjectIDs(&kDeviceObjectID, 1, inDataSize, outSize, outData);
        case kAudioDevicePropertyNominalSampleRate:
            return CopyFloat64(kSampleRate, inDataSize, outSize, outData);
        case kAudioDevicePropertyAvailableNominalSampleRates:
        {
            if (outSize == nullptr || outData == nullptr || inDataSize < sizeof(AudioValueRange))
                return kAudioHardwareBadPropertySizeError;
            auto* range = static_cast<AudioValueRange*>(outData);
            range->mMinimum = kSampleRate;
            range->mMaximum = kSampleRate;
            *outSize = sizeof(AudioValueRange);
            return noErr;
        }
        case kRasaBufferFrameSize:
            return CopyUInt32(kBufferFrameSize, inDataSize, outSize, outData);
        case kAudioDevicePropertyZeroTimeStampPeriod:
            return CopyUInt32(kZeroTimeStampPeriod, inDataSize, outSize, outData);
        case kAudioDevicePropertyClockAlgorithm:
            return CopyUInt32(kAudioDeviceClockAlgorithmRaw, inDataSize, outSize, outData);
        case kAudioDevicePropertyLatency:
        case kAudioDevicePropertySafetyOffset:
        case kAudioStreamPropertyLatency:
            return CopyUInt32(0, inDataSize, outSize, outData);
        case kAudioStreamPropertyStartingChannel:
            return CopyUInt32(1, inDataSize, outSize, outData);
        case kAudioStreamPropertyIsActive:
            return CopyUInt32(d->ioClients.load() > 0 ? 1 : 0, inDataSize, outSize, outData);
        case kAudioStreamPropertyDirection:
            return CopyUInt32(1, inDataSize, outSize, outData); // input
        case kAudioStreamPropertyTerminalType:
            return CopyUInt32('mic ', inDataSize, outSize, outData);
        case kAudioPlugInPropertyDeviceList:
            return CopyUInt32(kDeviceObjectID, inDataSize, outSize, outData);
        case kAudioPlugInPropertyBoxList:
            return CopyUInt32(kBoxObjectID, inDataSize, outSize, outData);
        case kAudioBoxPropertyAcquired:
            return CopyUInt32(1, inDataSize, outSize, outData);
        case kAudioBoxPropertyDeviceList:
            return CopyUInt32(kDeviceObjectID, inDataSize, outSize, outData);
        case kAudioPlugInPropertyTranslateUIDToDevice:
        {
            if (qualifier == nullptr || qualifierSize < sizeof(CFStringRef))
                return kAudioHardwareBadPropertySizeError;
            const auto uid = *static_cast<CFStringRef const*>(qualifier);
            const AudioObjectID device = CFStringCompare(uid, CFSTR("rasamic-mic"), 0) == kCFCompareEqualTo
                ? kDeviceObjectID : kAudioObjectUnknown;
            return CopyUInt32(device, inDataSize, outSize, outData);
        }
        case kAudioDevicePropertyStreams:
        {
            if (address->mScope != kAudioObjectPropertyScopeInput)
            {
                if (outSize != nullptr)
                    *outSize = 0;
                return noErr;
            }
            return CopyUInt32(kInputStreamObjectID, inDataSize, outSize, outData);
        }
        case kRasaStreamConfiguration:
        {
            const UInt32 need = StreamConfigurationSize(object, *address);
            if (outSize == nullptr || (need != 0 && (outData == nullptr || inDataSize < need)))
                return kAudioHardwareBadPropertySizeError;
            if (need == 0)
            {
                *outSize = 0;
                return noErr;
            }
            auto* list = static_cast<AudioBufferList*>(outData);
            bool hasStream = object == kDeviceObjectID && address->mScope != kAudioObjectPropertyScopeOutput;
            list->mNumberBuffers = hasStream ? 1 : 0;
            if (hasStream)
            {
                list->mBuffers[0].mNumberChannels = kChannels;
                list->mBuffers[0].mDataByteSize = kBufferFrameSize * sizeof(float);
                list->mBuffers[0].mData = nullptr;
            }
            *outSize = need;
            return noErr;
        }
        case kAudioStreamPropertyPhysicalFormat:
        case kAudioStreamPropertyVirtualFormat:
        {
            if (outSize == nullptr || outData == nullptr || inDataSize < sizeof(AudioStreamBasicDescription))
                return kAudioHardwareBadPropertySizeError;
            *static_cast<AudioStreamBasicDescription*>(outData) = NativeFormat();
            *outSize = sizeof(AudioStreamBasicDescription);
            return noErr;
        }
        case kAudioStreamPropertyAvailablePhysicalFormats:
        case kAudioStreamPropertyAvailableVirtualFormats:
        {
            if (outSize == nullptr || outData == nullptr || inDataSize < sizeof(AudioStreamRangedDescription))
                return kAudioHardwareBadPropertySizeError;
            auto* format = static_cast<AudioStreamRangedDescription*>(outData);
            format->mFormat = NativeFormat();
            format->mSampleRateRange.mMinimum = kSampleRate;
            format->mSampleRateRange.mMaximum = kSampleRate;
            *outSize = sizeof(AudioStreamRangedDescription);
            return noErr;
        }
        default:
            LogUnknownProperty(object, *address);
            return kAudioHardwareUnknownPropertyError;
    }
}

OSStatus STDMETHODCALLTYPE SetPropertyData(AudioServerPlugInDriverRef, AudioObjectID, pid_t, const AudioObjectPropertyAddress*, UInt32, const void*, UInt32, const void*)
{
    return kAudioHardwareUnsupportedOperationError;
}

OSStatus STDMETHODCALLTYPE StartIO(AudioServerPlugInDriverRef ref, AudioObjectID device, UInt32)
{
    if (device != kDeviceObjectID)
        return kAudioHardwareBadObjectError;
    auto* d = Self(ref);
    if (d->ioClients.fetch_add(1) == 0)
    {
        d->anchorHostTime = mach_absolute_time();
        d->timestampCount = 0;
        d->ring.Open();
    }
    return noErr;
}

OSStatus STDMETHODCALLTYPE StopIO(AudioServerPlugInDriverRef ref, AudioObjectID device, UInt32)
{
    if (device != kDeviceObjectID)
        return kAudioHardwareBadObjectError;
    auto* d = Self(ref);
    UInt32 clients = d->ioClients.load();
    while (clients > 0 && !d->ioClients.compare_exchange_weak(clients, clients - 1))
    {
    }
    if (d->ioClients.load() == 0)
        d->ring.Close();
    return noErr;
}

OSStatus STDMETHODCALLTYPE GetZeroTimeStamp(AudioServerPlugInDriverRef ref, AudioObjectID device, UInt32, Float64* outSampleTime, UInt64* outHostTime, UInt64* outSeed)
{
    if (device != kDeviceObjectID)
        return kAudioHardwareBadObjectError;
    if (outSampleTime == nullptr || outHostTime == nullptr || outSeed == nullptr)
        return kAudioHardwareBadPropertySizeError;
    auto* d = Self(ref);
    static mach_timebase_info_data_t timebase = [] {
        mach_timebase_info_data_t tb{};
        mach_timebase_info(&tb);
        return tb;
    }();
    Float64 sampleTime = static_cast<Float64>(d->timestampCount * kZeroTimeStampPeriod);
    Float64 ticksPerFrame = (1e9 / kSampleRate) * timebase.denom / timebase.numer;
    *outSampleTime = sampleTime;
    *outHostTime = d->anchorHostTime + static_cast<UInt64>(sampleTime * ticksPerFrame);
    *outSeed = 1;
    d->timestampCount += 1;
    return noErr;
}

OSStatus STDMETHODCALLTYPE WillDoIOOperation(AudioServerPlugInDriverRef, AudioObjectID device, UInt32, UInt32 operation, Boolean* outWillDo, Boolean* outWillDoInPlace)
{
    if (device != kDeviceObjectID)
        return kAudioHardwareBadObjectError;
    if (outWillDo == nullptr || outWillDoInPlace == nullptr)
        return kAudioHardwareBadPropertySizeError;
    // Native format is the canonical format, so the host needs no conversion
    // or processing from us; we only supply input samples.
    *outWillDo = operation == kAudioServerPlugInIOOperationReadInput;
    *outWillDoInPlace = true;
    return noErr;
}

OSStatus STDMETHODCALLTYPE BeginIOOperation(AudioServerPlugInDriverRef, AudioObjectID, UInt32, UInt32, UInt32, const AudioServerPlugInIOCycleInfo*)
{
    return noErr;
}

OSStatus STDMETHODCALLTYPE DoIOOperation(AudioServerPlugInDriverRef ref, AudioObjectID device, AudioObjectID stream, UInt32, UInt32 operation, UInt32 frameSize, const AudioServerPlugInIOCycleInfo*, void* ioMainBuffer, void*)
{
    if (device != kDeviceObjectID || stream != kInputStreamObjectID)
        return kAudioHardwareBadObjectError;
    if (operation != kAudioServerPlugInIOOperationReadInput)
        return kAudioHardwareIllegalOperationError;
    if (ioMainBuffer == nullptr)
        return kAudioHardwareBadPropertySizeError;
    auto* d = Self(ref);
    if (!d->ring.IsOpen())
    {
        // The server may start after coreaudiod; retry about once a second.
        if (++d->cyclesSinceOpenTry >= 48000 / kBufferFrameSize)
        {
            d->cyclesSinceOpenTry = 0;
            d->ring.Open();
        }
    }
    d->ring.Read(static_cast<float*>(ioMainBuffer), frameSize);
    return noErr;
}

OSStatus STDMETHODCALLTYPE EndIOOperation(AudioServerPlugInDriverRef, AudioObjectID, UInt32, UInt32, UInt32, const AudioServerPlugInIOCycleInfo*)
{
    return noErr;
}

const AudioServerPlugInDriverInterface kDriverInterface = {
    nullptr, // _reserved
    QueryInterface,
    AddRef,
    Release,
    Initialize,
    CreateDevice,
    DestroyDevice,
    AddDeviceClient,
    RemoveDeviceClient,
    PerformDeviceConfigurationChange,
    AbortDeviceConfigurationChange,
    HasProperty,
    IsPropertySettable,
    GetPropertyDataSize,
    GetPropertyData,
    SetPropertyData,
    StartIO,
    StopIO,
    GetZeroTimeStamp,
    WillDoIOOperation,
    BeginIOOperation,
    DoIOOperation,
    EndIOOperation,
};

} // namespace

// Factory UUID must match CFPlugInFactories in Info.plist.
#define kRasaFactoryUUID CFUUIDGetConstantUUIDWithBytes(nullptr, 0x7C, 0x9E, 0x2A, 0x41, 0x5B, 0x3D, 0x4E, 0x8F, 0x9A, 0x6C, 0x2D, 0x4E, 0x6F, 0x8A, 0x0B, 0x1C)

extern "C" void* RasaMicHalPluginFactory(CFAllocatorRef, CFUUIDRef typeUUID)
{
    if (!CFEqual(typeUUID, kAudioServerPlugInTypeUUID))
        return nullptr;
    auto* d = new RasaDriver();
    d->interface = &kDriverInterface;
    CFPlugInAddInstanceForFactory(kRasaFactoryUUID);
    return d;
}