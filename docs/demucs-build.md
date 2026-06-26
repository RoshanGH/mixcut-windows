# demucs.cpp win-x64 构建配方（人声/BGM 分离引擎）

配音 v0.5.0 的人声分离引擎。产物 `demucs.exe` 已固化在 `src/MixCut/Resources/bin/demucs.exe`
（csproj 用 `Resources\bin\**\*` 通配符自动拷贝到输出 `bin/`，无需改 csproj）。
本文记录**如何从源码重建**（升级模型/编译器时用），对齐 macOS 同款 demucs.cpp。

## 来源
- 仓库：`https://github.com/sevagh/demucs.cpp`（C++17 + Eigen + ggml 权重格式 + OpenMP）
- 子模块（必需）：`vendor/eigen`(gitlab，头文件)、`vendor/libnyquist`(wav IO，含 in-tree 第三方解码器)
  - 非必需：`vendor/googletest`(测试)、`vendor/demucs`(python 权重转换脚本)
- CLI 签名：`demucs.exe <model.bin> <input.wav> <outDir>` → 输出 `target_0_drums/1_bass/2_other/3_vocals.wav`
  - 与 macOS `VocalSeparationService.swift` 调用完全一致；BGM = `amix(drums,bass,other,normalize=0)`

## 模型
- `ggml-htdemucs-4s.bin`（~80MB，htdemucs 4-source）
- 国内镜像（铁律，勿用 huggingface 直链）：
  `https://hf-mirror.com/datasets/Retrobear/demucs.cpp/resolve/main/ggml-model-htdemucs-4s-f16.bin`
- 运行时落 `%LOCALAPPDATA%\MixCut\demucs-models\ggml-htdemucs-4s.bin`，首次用配音时下载（复用 ASRService 的镜像+Range 续传）

## 工具链
- Visual Studio 2022 Build Tools，工作负载 `Microsoft.VisualStudio.Workload.VCTools` +
  组件 `Microsoft.VisualStudio.Component.VC.CMake.Project`（自带 CMake+Ninja）+ Windows 11 SDK
- winget 一键：
  ```
  winget install --id Microsoft.VisualStudio.2022.BuildTools -e --override "--quiet --wait --norestart --add Microsoft.VisualStudio.Workload.VCTools --add Microsoft.VisualStudio.Component.VC.CMake.Project --add Microsoft.VisualStudio.Component.Windows11SDK.22621 --includeRecommended"
  ```

## 对 CMakeLists.txt 的 3 处必要补丁（MSVC 兼容 + 分发安全）
原 CMakeLists 面向 gcc/clang，MSVC 下要改：

1. **编译器标志块必须移到 `project()` 之后**（`MSVC` 变量在 `project()` 检测编译器后才有值；
   否则落进 else 分支把 `-Wall -Wextra` 喂给 cl.exe 报 D8021）。改为：
   ```cmake
   if(MSVC)
     set(CMAKE_CXX_FLAGS "/W3 /EHsc /bigobj /utf-8")
     set(CMAKE_CXX_FLAGS_RELEASE "/O2 /fp:fast /arch:AVX2 /DNDEBUG")
   else()
     ... -march=x86-64-v3 ...   # 不要 -march=native（分发地雷，见下）
   endif()
   ```
2. **绝不用 `-march=native` / 不针对构建机 CPU 优化**：会生成 AVX-512 等指令，在不支持的用户机崩。
   统一 **AVX2 安全基线**（`/arch:AVX2`），与现有 whisper-cli 的 AVX2 要求一致。
3. **去掉 gtest 测试目标**（未拉 `vendor/googletest`，构建主程序不需要）：删掉 `add_subdirectory(vendor/googletest)`
   及 `demucs.cpp.test` 相关块。

## 对源码的 1 处补丁（MSVC 严格性）
- `cli-apps/demucs.cpp` 约 230 行：`write_audio_file(target_waveform, p_target)` →
  `write_audio_file(target_waveform, p_target.string())`（`std::filesystem::path` 不隐式转 `std::string`）。

## 配置 + 构建（vcvars64 环境内）
```bat
call "...\BuildTools\VC\Auxiliary\Build\vcvars64.bat"
cd <demucs.cpp>\build
cmake -G Ninja -DCMAKE_BUILD_TYPE=Release -DUSE_OPENBLAS=OFF ^
      -DCMAKE_POLICY_DEFAULT_CMP0091=NEW -DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreadedDLL ..
cmake --build . --target demucs.cpp.main --config Release
```
- `USE_OPENBLAS=OFF`：走 OpenMP-only 分支（Windows 无 OpenBLAS）。
- `CMAKE_MSVC_RUNTIME_LIBRARY=MultiThreadedDLL` + `CMP0091=NEW`：**强制所有子项目统一动态 /MD**。
  否则 libnyquist 的 C 依赖(Vorbis/Opus/Flac)与主程序 CRT 不一致 → 链接报一堆 `__imp_*` 未解析。
  /MD 也正好与已打包的 vcruntime140/msvcp140 + whisper-cli 一致。
- 产物：`build\demucs.cpp.main.exe` → 重命名 `demucs.exe` 放 `src/MixCut/Resources/bin/`。

## 依赖（PE import 扫描确认，均已随包）
`msvcp140.dll` / `vcomp140.dll`(OpenMP) / `vcruntime140.dll` / `vcruntime140_1.dll` +
`api-ms-win-crt-*`(UCRT，Win10/11 默认存在)。**无缺失**，干净 Win10/11 可跑。
新建/升级二进制后必跑 `scripts/smoke/Get-PeImports.ps1 -Path demucs.exe -CompareDir publish\bin`。

## 性能
- CPU 推理约 **9x 实时**（实测 75s 广告 ~680s）。设计上「整轨按 ContentHash 只分一次 + 缓存」缓解。
- 提速方向：构建多线程版 `demucs_mt.cpp.main`（CLI 多一个线程数参数），后续 P2 可选。
