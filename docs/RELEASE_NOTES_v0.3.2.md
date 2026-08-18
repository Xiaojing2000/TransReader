# 译页 TransReader v0.3.2

v0.3.2 修复安装版 OCR 初始化失败，并把大型本地能力改造成可以独立安装、开关和修复的组件。

## 下载

- **`TransReader-v0.3.2-win-x64-setup.exe`**：Windows x64 基础安装包，不再内置大型 OCR 运行库和模型。
- **`TransReader-OCR-PP-OCRv5-mobile-win-x64.zip`**：PP-OCRv5 mobile 离线组件；一般用户也可让应用自动下载。
- **`TransReader-v0.3.2-SHA256SUMS.txt`**：同时校验上述两个文件。

继续不提供 Portable 便携版。覆盖安装会保留 `%LOCALAPPDATA%\TransReader` 中的设置、文献库和缓存。

## 重点变化

- 安装后的 OCR 使用绝对 `OCR.yaml` 路径启动，不再依赖开发机源码目录。
- OCR 启动增加 60 秒超时、真实原生错误输出、失败后重试和重新载入冒烟检测。
- 基础安装目录从约 499 MB 降至约 166 MB；OCR 运行库和模型首次使用时按需安装。
- OCR 支持官方 Release、`ghproxy.net` 镜像、断点续传、SHA-256 校验和离线 ZIP 导入。
- “AI 中心 → 本地组件”提供 OCR 与本地大模型独立开关，以及安装/智能修复、校验、重新载入、强制重装和卸载。
- 新增 Tencent Hy-MT2 1.8B Q4_K_M 专业翻译模型。综合约 1.06 GiB 下载体积、普通电脑内存需求和 llama.cpp 兼容性，本版本选择 1.8B，而不是约 4.3 GiB 的 7B 版本。
- Qwen3 1.7B 继续负责阅读问答和文献库整理；两个模型可分别安装并共用推理运行时。

## 升级说明

从 v0.3.1 覆盖升级时，应用会校验并迁移安装目录中已有的 OCR 文件，成功后才清理旧副本，因此通常无需重复下载。若组件无法使用，进入“本地组件”先点“重新载入”，再点“安装 / 智能修复”；仍失败时可强制重装或导入离线包。

---

## English summary

TransReader v0.3.2 fixes installed-build OCR startup by passing an absolute PaddleOCR pipeline configuration. OCR is now a verified on-demand component, reducing the base installation from roughly 499 MB to 166 MB. Independent OCR/local-model switches and repair/reload controls are available under Local Components. Tencent Hy-MT2 1.8B Q4_K_M is added as the recommended local translation model, while Qwen3 1.7B remains dedicated to questions and library analysis. The Release contains one Setup package, one offline OCR component, and a checksum manifest; no Portable ZIP is published.
