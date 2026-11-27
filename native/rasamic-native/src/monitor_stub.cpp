#include "rasamic_native.h"

/* The iOS client only captures; speaker monitor output is a server feature and
 * miniaudio's desktop backends are not available on iOS. */

extern "C" {

void* rm_monitor_create(int32_t, int32_t) { return nullptr; }
void* rm_monitor_create_named(int32_t, int32_t, const char*) { return nullptr; }
void* rm_monitor_create_id(int32_t, int32_t, const char*) { return nullptr; }
int32_t rm_monitor_write(void*, const float*, int32_t) { return -1; }
void rm_monitor_destroy(void*) {}
void rm_monitor_get_stats(void*, rm_monitor_stats* out) {
    if (out) *out = {};
}
int32_t rm_devices_enumerate(int32_t, rm_device_callback, void*) { return -1; }

} /* extern "C" */