# Third-Party Notices

Rasa (RasaMic) is MIT-licensed. It incorporates or depends on the following
third-party components. Native components are linked into `rasamic_native`
shared libraries; managed components are consumed as NuGet packages.

| Component | Purpose | License | Source |
|---|---|---|---|
| libopus | Audio codec | BSD-3-Clause | https://github.com/xiph/opus |
| RNNoise | Noise suppression | BSD-3-Clause | https://github.com/xiph/rnnoise |
| SpeexDSP | AGC, resampler | BSD-3-Clause | https://github.com/xiph/speexdsp |
| Oboe | Android AAudio capture | Apache-2.0 | https://github.com/google/oboe |
| miniaudio | Server monitor output / Linux audio feed | MIT-0 | https://github.com/mackron/miniaudio |
| Virtual-Audio-Driver (fork) | Windows virtual mic driver | MIT | https://github.com/VirtualDrivers/Virtual-Audio-Driver |
| Apple AudioServerPlugIn sample | macOS HAL plugin basis | Apple MIT-style sample license | https://developer.apple.com/library/archive/samplecode/AudioDriverExamples |
| Avalonia | Server desktop GUI | MIT | https://github.com/AvaloniaUI/Avalonia |
| CommunityToolkit.Mvvm | MVVM helpers | MIT | https://github.com/CommunityToolkit/dotnet |
| Haukcode.Mdns | mDNS advertise/browse | MIT | https://github.com/HakanL/Haukcode.Mdns |
| QRCoder | Pairing QR codes | MIT | https://github.com/codebude/QRCoder |
| SharpHook | Global hotkeys | MIT | https://github.com/TolikPylypchuk/SharpHook |
| InTheHand.Net.Bluetooth (32feet) | PC-side Bluetooth RFCOMM listener | MIT | https://github.com/inthehand/32feet |
| Velopack | Windows GUI installer + auto-update | MIT | https://github.com/velopack/velopack |
| System.Numerics.Tensors | SIMD PCM post-gain (TensorPrimitives) | MIT | https://github.com/dotnet/runtime |

Full license texts for native components ship alongside their sources in
`native/third_party/` (git submodules). The Windows driver fork retains its
upstream MIT copyright (c) 2025 MikeTheTech.