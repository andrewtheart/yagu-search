# PaddleSharp 🌟 [![main](https://github.com/sdcb/PaddleSharp/actions/workflows/main.yml/badge.svg)](https://github.com/sdcb/PaddleSharp/actions/workflows/main.yml) [![QQ](https://img.shields.io/badge/QQ_Group-579060605-52B6EF?style=social&logo=tencent-qq&logoColor=000&logoWidth=20)](https://jq.qq.com/?_wv=1027&k=K4fBqpyQ)

**[English](README.md)** | **简体中文**

💗 为 `PaddleInference` C API 提供的 .NET 包装，支持 **Windows**(x64) 💻，基于NVIDIA Cuda 10.2+ 的 GPU 🎮 和 **Linux**(Ubuntu-22.04 x64) 🐧，当前包含以下主要组件：

* [PaddleOCR 📖](./docs/ocr.md) 支持14种OCR语言模型的按需下载，允许旋转文本角度检测，180度文本检测，同时也支持表格识别📊。
* [PaddleDetection 🎯](./docs/detection.md) 支持PPYolo检测模型和PicoDet模型🏹。
* [RotationDetection 🔄](./docs/rotation-detection.md) 使用百度官方的 `text_image_orientation_infer` 模型来检测文本图片的旋转角度(`0, 90, 180, 270`)。
* [PaddleNLP中文分词 📚](./docs/paddlenlp-lac.md) 支持`PaddleNLP`LAC中文分词模型，支持词性标注、自定义词典等功能。
* [Paddle2Onnx 🔄](./docs/paddle2onnx.md) 允许用户使用 `C#` 导出 `ONNX` 模型。

## NuGet 包/Docker 镜像 📦

### 发布说明 📝
请查看 [此页面 📄](https://github.com/sdcb/PaddleSharp/releases)。

### 基础设施包 🏗️

| NuGet 包 💼           | 版本 📌                                                                                                               | 描述 📚                             |
| -------------------- | -------------------------------------------------------------------------------------------------------------------- | ---------------------------------- |
| Sdcb.PaddleInference | [![NuGet](https://img.shields.io/nuget/v/Sdcb.PaddleInference.svg)](https://nuget.org/packages/Sdcb.PaddleInference) | Paddle Inference C API .NET 绑定 ⚙️ |

### 原生包 🏗️

| 包名                                                     | 版本 📌                                                                                                                                                                    | 描述                                  |
| ------------------------------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ----------------------------------- |
| Sdcb.PaddleInference.runtime.win64.mkl                  | [![NuGet](https://img.shields.io/nuget/v/Sdcb.PaddleInference.runtime.win64.mkl.svg)](https://nuget.org/packages/Sdcb.PaddleInference.runtime.win64.mkl)                   | 推荐给大多数用户（CPU、MKL）            |
| Sdcb.PaddleInference.runtime.win64.openblas             | [![NuGet](https://img.shields.io/nuget/v/Sdcb.PaddleInference.runtime.win64.openblas.svg)](https://nuget.org/packages/Sdcb.PaddleInference.runtime.win64.openblas)         | CPU，OpenBLAS                       |
| Sdcb.PaddleInference.runtime.win64.openblas-noavx       | [![NuGet](https://img.shields.io/nuget/v/Sdcb.PaddleInference.runtime.win64.openblas-noavx.svg)](https://nuget.org/packages/Sdcb.PaddleInference.runtime.win64.openblas-noavx) | CPU，无AVX，适用于老款CPU               |
| Sdcb.PaddleInference.runtime.win64.cu118_cudnn89_sm61   | [![NuGet](https://img.shields.io/nuget/v/Sdcb.PaddleInference.runtime.win64.cu118_cudnn89_sm61.svg)](https://nuget.org/packages/Sdcb.PaddleInference.runtime.win64.cu118_cudnn89_sm61) | CUDA 11.8，GTX 10系列                 |
| Sdcb.PaddleInference.runtime.win64.cu118_cudnn89_sm75   | [![NuGet](https://img.shields.io/nuget/v/Sdcb.PaddleInference.runtime.win64.cu118_cudnn89_sm75.svg)](https://nuget.org/packages/Sdcb.PaddleInference.runtime.win64.cu118_cudnn89_sm75) | CUDA 11.8，RTX 20/GTX 16xx系列         |
| Sdcb.PaddleInference.runtime.win64.cu118_cudnn89_sm86   | [![NuGet](https://img.shields.io/nuget/v/Sdcb.PaddleInference.runtime.win64.cu118_cudnn89_sm86.svg)](https://nuget.org/packages/Sdcb.PaddleInference.runtime.win64.cu118_cudnn89_sm86) | CUDA 11.8，RTX 30系列                 |
| Sdcb.PaddleInference.runtime.win64.cu118_cudnn89_sm89   | [![NuGet](https://img.shields.io/nuget/v/Sdcb.PaddleInference.runtime.win64.cu118_cudnn89_sm89.svg)](https://nuget.org/packages/Sdcb.PaddleInference.runtime.win64.cu118_cudnn89_sm89) | CUDA 11.8，RTX 40系列                 |
| Sdcb.PaddleInference.runtime.win64.cu126_cudnn95_sm61   | [![NuGet](https://img.shields.io/nuget/v/Sdcb.PaddleInference.runtime.win64.cu126_cudnn95_sm61.svg)](https://nuget.org/packages/Sdcb.PaddleInference.runtime.win64.cu126_cudnn95_sm61) | CUDA 12.6，GTX 10系列                 |
| Sdcb.PaddleInference.runtime.win64.cu126_cudnn95_sm75   | [![NuGet](https://img.shields.io/nuget/v/Sdcb.PaddleInference.runtime.win64.cu126_cudnn95_sm75.svg)](https://nuget.org/packages/Sdcb.PaddleInference.runtime.win64.cu126_cudnn95_sm75) | CUDA 12.6，RTX 20/GTX 16xx系列         |
| Sdcb.PaddleInference.runtime.win64.cu126_cudnn95_sm86   | [![NuGet](https://img.shields.io/nuget/v/Sdcb.PaddleInference.runtime.win64.cu126_cudnn95_sm86.svg)](https://nuget.org/packages/Sdcb.PaddleInference.runtime.win64.cu126_cudnn95_sm86) | CUDA 12.6，RTX 30系列                 |
| Sdcb.PaddleInference.runtime.win64.cu126_cudnn95_sm89   | [![NuGet](https://img.shields.io/nuget/v/Sdcb.PaddleInference.runtime.win64.cu126_cudnn95_sm89.svg)](https://nuget.org/packages/Sdcb.PaddleInference.runtime.win64.cu126_cudnn95_sm89) | CUDA 12.6，RTX 40系列                 |
| Sdcb.PaddleInference.runtime.win64.cu129_cudnn910_sm61  | [![NuGet](https://img.shields.io/nuget/v/Sdcb.PaddleInference.runtime.win64.cu129_cudnn910_sm61.svg)](https://nuget.org/packages/Sdcb.PaddleInference.runtime.win64.cu129_cudnn910_sm61) | CUDA 12.9，GTX 10系列                 |
| Sdcb.PaddleInference.runtime.win64.cu129_cudnn910_sm75  | [![NuGet](https://img.shields.io/nuget/v/Sdcb.PaddleInference.runtime.win64.cu129_cudnn910_sm75.svg)](https://nuget.org/packages/Sdcb.PaddleInference.runtime.win64.cu129_cudnn910_sm75) | CUDA 12.9，RTX 20/GTX 16xx系列         |
| Sdcb.PaddleInference.runtime.win64.cu129_cudnn910_sm86  | [![NuGet](https://img.shields.io/nuget/v/Sdcb.PaddleInference.runtime.win64.cu129_cudnn910_sm86.svg)](https://nuget.org/packages/Sdcb.PaddleInference.runtime.win64.cu129_cudnn910_sm86) | CUDA 12.9，RTX 30系列                 |
| Sdcb.PaddleInference.runtime.win64.cu129_cudnn910_sm89  | [![NuGet](https://img.shields.io/nuget/v/Sdcb.PaddleInference.runtime.win64.cu129_cudnn910_sm89.svg)](https://nuget.org/packages/Sdcb.PaddleInference.runtime.win64.cu129_cudnn910_sm89) | CUDA 12.9，RTX 40系列                 |
| Sdcb.PaddleInference.runtime.win64.cu129_cudnn910_sm120 | [![NuGet](https://img.shields.io/nuget/v/Sdcb.PaddleInference.runtime.win64.cu129_cudnn910_sm120.svg)](https://nuget.org/packages/Sdcb.PaddleInference.runtime.win64.cu129_cudnn910_sm120) | CUDA 12.9，RTX 50系列（仅对应 CUDA 12.9）|
| Sdcb.PaddleInference.runtime.linux-x64.openblas         | [![NuGet](https://img.shields.io/nuget/v/Sdcb.PaddleInference.runtime.linux-x64.openblas.svg)](https://nuget.org/packages/Sdcb.PaddleInference.runtime.linux-x64.openblas)   | Linux x64，OpenBLAS                  |
| Sdcb.PaddleInference.runtime.linux-x64.mkl              | [![NuGet](https://img.shields.io/nuget/v/Sdcb.PaddleInference.runtime.linux-x64.mkl.svg)](https://nuget.org/packages/Sdcb.PaddleInference.runtime.linux-x64.mkl)             | Linux x64，MKL                       |
| Sdcb.PaddleInference.runtime.linux-x64                  | [![NuGet](https://img.shields.io/nuget/v/Sdcb.PaddleInference.runtime.linux-x64.svg)](https://nuget.org/packages/Sdcb.PaddleInference.runtime.linux-x64)                     | Linux x64，MKL+OpenVINO               |
| Sdcb.PaddleInference.runtime.linux-arm64                | [![NuGet](https://img.shields.io/nuget/v/Sdcb.PaddleInference.runtime.linux-arm64.svg)](https://nuget.org/packages/Sdcb.PaddleInference.runtime.linux-arm64)                 | Linux ARM64                          |
| Sdcb.PaddleInference.runtime.osx-x64                    | [![NuGet](https://img.shields.io/nuget/v/Sdcb.PaddleInference.runtime.osx-x64.svg)](https://nuget.org/packages/Sdcb.PaddleInference.runtime.osx-x64)                         | macOS x64，内含ONNXRuntime            |
| Sdcb.PaddleInference.runtime.osx-arm64                  | [![NuGet](https://img.shields.io/nuget/v/Sdcb.PaddleInference.runtime.osx-arm64.svg)](https://nuget.org/packages/Sdcb.PaddleInference.runtime.osx-arm64)                     | macOS ARM64                          |

**包选择指南：**

- 推荐大多数用户使用`Sdcb.PaddleInference.runtime.win64.mkl`。该包在性能和体积之间实现了最佳平衡。需要注意该包不支持GPU加速，适合普遍场景。
- `openblas-noavx`适合不支持AVX2指令集的老CPU。
- 其余包为不同CUDA组合（GPU加速），支持以下三种CUDA版本：
  - **CUDA 11.8：** 支持10–40系列NVIDIA显卡
  - **CUDA 12.6：** 支持10–40系列NVIDIA显卡
  - **CUDA 12.9：** 支持10–50系列NVIDIA显卡

**重要说明：**  
并非所有GPU包都适用于每一款显卡。请参考以下GPU与`sm`后缀的对应关系：

| `sm` 后缀 | 支持显卡系列                              |
|-----------|---------------------------------------|
| sm61      | GTX 10系列                             |
| sm75      | RTX 20系列（及GTX 16xx系列如GTX 1660）         |
| sm86      | RTX 30系列                             |
| sm89      | RTX 40系列                             |
| sm120     | RTX 50系列（仅CUDA 12.9支持）             |

其他以 `Sdcb.PaddleInference.runtime` 开头的包可能已弃用。

所有这些包都是由我手动编译的，并且使用了此处的一些代码补丁：https://github.com/sdcb/PaddleSharp/blob/master/build/capi.patch

## Paddle 设备

* Mkldnn - `PaddleDevice.Mkldnn()`
  
  基于 [Mkldnn](https://github.com/oneapi-src/oneDNN)，一般来说很快

* Blas - `PaddleDevice.Blas()`

  基于 [openblas](https://www.openblas.net/)，或者mkldnn blas，较慢，但依赖文件更小，内存消耗更少

* Onnx - `PaddleDevice.Onnx()`

  基于 [onnxruntime](https://github.com/microsoft/onnxruntime)，也很快，内存消耗更少

* Gpu - `PaddleDevice.Gpu()`

  更快，但依赖 NVIDIA GPU 和 CUDA

  如果你想使用 GPU，你应该参考 FAQ 中的 `如何启用 GPU?` 部分，CUDA/cuDNN/TensorRT 需要手动安装。

## 常见问题 ❓
### 为什么我的代码在我自己的 windows 机器上运行良好，但在其他机器上出现 DllNotFoundException: 💻
1. 请确保 `Windows` 上已安装[最新的 Visual C++ 运行库](https://aka.ms/vs/17/release/vc_redist.x64.exe) (如果你已经安装了 `Visual Studio`，通常这会自动安装) 🛠️
否则，会出现以下错误（仅限 Windows）：
   ```
   DllNotFoundException: 无法加载 DLL 'paddle_inference_c' 或其依赖项之一 (0x8007007E)
   ```
   
   如果遇到无法加载 DLL OpenCvSharpExtern.dll 或其依赖项之一的问题，那么可能是 Windows Server 2012 R2 机器上没有安装 Media Foundation：<img width="830" alt="image" src="https://user-images.githubusercontent.com/1317141/193706883-6a71ea83-65d9-448b-afee-2d25660430a1.png">

2. 许多旧的 CPU 不支持 AVX 指令集，请确保你的 CPU 支持 AVX，或者下载 x64-noavx-openblas DLLs 并禁用 Mkldnn: `PaddleDevice.Openblas()` 🚀

3. 如果你正在使用 **Win7-x64**，并且你的 CPU 支持 AVX2，那么你可能还需要将以下3个 DLLs 提取到 `C:\Windows\System32` 文件夹中才能运行：💾
   * api-ms-win-core-libraryloader-l1-2-0.dll
   * api-ms-win-core-processtopology-obsolete-l1-1-0.dll
   * API-MS-Win-Eventing-Provider-L1-1-0.dll
   
   你可以在这里下载这3个 DLLs：[win7-x64-onnxruntime-missing-dlls.zip](https://github.com/sdcb/PaddleSharp/files/10110622/win7-x64-onnxruntime-missing-dlls.zip) ⬇️

### 如何启用 GPU? 🎮
启用 GPU 支持可以显著提高吞吐量并降低 CPU 使用率。🚀

在 Windows 中使用 GPU 的步骤：
1. （对于 Windows）安装包：`Sdcb.PaddleInference.runtime.win64.cu120*` 代替 `Sdcb.PaddleInference.runtime.win64.mkl`，**不要** 同时安装。📦
2. 从 NVIDIA 安装 CUDA，并将环境变量配置到 `PATH` 或 `LD_LIBRARY_PATH` (Linux) 🔧
3. 从 NVIDIA 安装 cuDNN，并将环境变量配置到 `PATH` 或 `LD_LIBRARY_PATH` (Linux) 🛠️
4. 从 NVIDIA 安装 TensorRT，并将环境变量配置到 `PATH` 或 `LD_LIBRARY_PATH` (Linux) ⚙️

你可以参考这个博客页面了解在 Windows 中使用 GPU：[关于PaddleSharp GPU使用 常见问题记录](https://www.cnblogs.com/cuichaohui/p/15766519.html) 📝

如果你正在使用 Linux，你需要根据 [docker 构建脚本](./build/docker/dotnet6sdk-paddle/Dockerfile) 编译自己的 OpenCvSharp4 环境，并完成 CUDA/cuDNN/TensorRT 的配置任务。🐧

完成这些步骤后，你可以尝试在 paddle device 配置参数中指定 `PaddleDevice.Gpu()`，然后享受性能提升！🎉

## 感谢 & 赞助商 🙏
* 崔亮  https://github.com/cuiliang
* 梁现伟
* 深圳-钱文松
* iNeuOS工业互联网操作系统：http://www.ineuos.net

## 联系方式 📞
C#/.NET 计算机视觉技术交流群：**579060605**
![](./assets/qq.png)
