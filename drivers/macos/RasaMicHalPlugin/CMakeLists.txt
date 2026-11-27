cmake_minimum_required(VERSION 3.20)
project(RasaMicHalPlugin LANGUAGES CXX)

if(NOT APPLE)
    message(FATAL_ERROR "RasaMicHalPlugin is a macOS coreaudiod plugin; build it on macOS.")
endif()

add_library(RasaMicHalPlugin MODULE RasaMicHalPlugin.cpp)
set_target_properties(RasaMicHalPlugin PROPERTIES
    CXX_STANDARD 17
    CXX_STANDARD_REQUIRED YES
    BUNDLE TRUE
    MACOSX_BUNDLE ON
    BUNDLE_EXTENSION driver
    MACOSX_BUNDLE_INFO_PLIST "${CMAKE_CURRENT_SOURCE_DIR}/Info.plist"
    OUTPUT_NAME RasaMicHalPlugin)
target_link_libraries(RasaMicHalPlugin PRIVATE
    "-framework CoreFoundation"
    "-framework CoreAudio")

# Ad-hoc signing lets CI load the bundle without release credentials.
add_custom_command(TARGET RasaMicHalPlugin POST_BUILD
    COMMAND codesign --force --sign - "${CMAKE_CURRENT_BINARY_DIR}/RasaMicHalPlugin.driver"
    VERBATIM)

add_executable(RasaMicHalPluginSelfTest SelfTest.cpp)
target_link_libraries(RasaMicHalPluginSelfTest PRIVATE
    "-framework CoreFoundation"
    "-framework CoreAudio")
