#include <CoreAudio/AudioServerPlugIn.h>
#include <CoreFoundation/CoreFoundation.h>
#include <dlfcn.h>

#include <cstdio>

int main(int argc, char** argv)
{
    if (argc != 2)
    {
        std::fprintf(stderr, "usage: RasaMicHalPluginSelfTest <bundle executable>\n");
        return 2;
    }

    void* library = dlopen(argv[1], RTLD_NOW | RTLD_LOCAL);
    if (library == nullptr)
    {
        std::fprintf(stderr, "dlopen failed: %s\n", dlerror());
        return 1;
    }

    using Factory = void* (*)(CFAllocatorRef, CFUUIDRef);
    auto factory = reinterpret_cast<Factory>(dlsym(library, "RasaMicHalPluginFactory"));
    if (factory == nullptr)
    {
        std::fprintf(stderr, "factory symbol missing: %s\n", dlerror());
        return 1;
    }

    void* instance = factory(kCFAllocatorDefault, kAudioServerPlugInTypeUUID);
    if (instance == nullptr)
    {
        std::fprintf(stderr, "factory rejected the AudioServerPlugIn type\n");
        return 1;
    }
    auto* driver = *static_cast<AudioServerPlugInDriverInterface**>(instance);
    if (driver == nullptr)
    {
        std::fprintf(stderr, "factory returned no driver interface\n");
        return 1;
    }
    AudioServerPlugInDriverRef driverRef = &driver;

    OSStatus status = driver->Initialize(driverRef, nullptr);
    if (status != noErr)
    {
        std::fprintf(stderr, "Initialize failed: %d\n", status);
        return 1;
    }

    const AudioObjectPropertyAddress deviceList = {
        kAudioPlugInPropertyDeviceList,
        kAudioObjectPropertyScopeGlobal,
        kAudioObjectPropertyElementMain};
    if (!driver->HasProperty(driverRef, kAudioObjectPlugInObject, 0, &deviceList))
    {
        std::fprintf(stderr, "device-list property is not supported\n");
        return 1;
    }

    UInt32 size = 0;
    status = driver->GetPropertyDataSize(
        driverRef, kAudioObjectPlugInObject, 0, &deviceList, 0, nullptr, &size);
    if (status != noErr || size != sizeof(AudioObjectID))
    {
        std::fprintf(stderr, "device-list size failed: status=%d size=%u\n", status, size);
        return 1;
    }

    AudioObjectID device = kAudioObjectUnknown;
    status = driver->GetPropertyData(
        driverRef, kAudioObjectPlugInObject, 0, &deviceList, 0, nullptr, size, &size, &device);
    if (status != noErr || device != 2)
    {
        std::fprintf(stderr, "device-list data failed: status=%d device=%u\n", status, device);
        return 1;
    }

    std::printf("HAL self-test passed: device=%u\n", device);
    driver->Release(driverRef);
    dlclose(library);
    return 0;
}
