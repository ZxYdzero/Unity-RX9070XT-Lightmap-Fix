# Unity Lightmap Bake Fix Tool (RX 9070 XT / AMD OpenCL 修复工具)

一款专为 **Unity（含 VRChat SDK 2022.3）** 打造的编辑器光照烘焙一键诊断与修复插件，重点解决 **AMD RX 9070 XT（RDNA 4）等新一代 A 卡** 在 Unity Progressive GPU 光照烘焙中出现的 OpenCL 兼容性崩溃与卡死问题。

---

## 解决的核心问题

在 AMD RX 9070 XT 等新架构显卡上使用 Unity 默认 Progressive GPU（OpenCL）烘焙光照时，常会遇到以下问题：

1. **OpenCL 缓冲断言报错**：即使显存非常充足，依然报错  
   `Assertion failed on expression: 'openCLRenderBuffers.HasBakingBuffers()'`
2. **烘焙进度永久卡在 0%**：RadeonPro 降噪器或 OpenCL 内核初始化失败导致卡死。
3. **Baked GI 与 Realtime GI 冲突**：同时勾选导致编辑或刷新资源时反复重建缓冲并报错。
4. **异常物体误开 `Contribute GI`**：缺少 UV2、已禁用渲染器或超大尺寸的外景/地形网格拖垮烘焙速度与内存。
5. **Shadowmask 阴影缺失**：普通 `Shadowmask` 模式下，已烘焙的静态建筑无法向未烘焙的动态地面投射实时阴影。

---

## 主要功能

- **一键环境与场景诊断**：自动读取当前 GPU / 显存 / CPU 线程 / 内存、LightingSettings 后端、降噪器配置、LightmapParameters 采样强度、Shadowmask 模式，并扫描全场景异常 `Contribute GI` 物体（支持一键在 Hierarchy 中高亮定位）。
- **一键稳定修复**：
  - 自动切换至稳定的 `Progressive CPU + OpenImageDenoise` 降噪器（规避新 A 卡 OpenCL / RadeonPro 兼容问题）；
  - 关闭冲突的 `Realtime GI`；
  - 将 `QualitySettings.shadowmaskMode` 切换为 `DistanceShadowmask`，保证静态建筑向动态地面正常投射实时阴影；
  - 智能清理无 UV2 / 禁用渲染器 / 超大尺寸物体的 `Contribute GI` 标记（内置室内建筑与家具名称白名单保护，不会误伤室内网格）；
  - 自动清理损坏的 GI 磁盘缓存，可选修复后直接启动异步烘焙。
- **快捷手动控制**：支持随时切换烘焙后端、同步降噪器、停止当前烘焙、清理 GI Disk Cache 与启动异步烘焙。

---

## 安装方式

### 方式一：通过 Unity Package Manager（UPM）直接安装（推荐）

1. 打开 Unity 顶部菜单：`Window -> Package Manager`。
2. 点击左上角 `+` 号，选择 `Add package from git URL...`。
3. 输入本仓库地址：`https://github.com/ZxYdzero/Unity-RX9070XT-Lightmap-Fix.git` 点击 `Add` 即可。

### 方式二：单脚本直接放入项目

1. 下载本仓库中的 [`Editor/LightmapBakeFixToolWindow.cs`](Editor/LightmapBakeFixToolWindow.cs)。
2. 将其放入你 Unity 项目的 `Assets/Editor/` 目录下，等待编译完成即可。

---

## 使用方法

1. 在 Unity 顶部菜单栏打开：**`工具 -> 光照烘焙一键诊断与修复`**。
2. 查看「1. 当前环境与问题诊断」自动扫描出的风险项。
3. 在「2. 一键稳定修复（推荐）」中按需勾选修复选项与目标分辨率，点击 **「一键应用修复并开始烘焙」**（或「仅应用修复」）。

---

## 适用版本

- Unity `2022.3 LTS`（已针对 VRChat World SDK 2022.3.22f1 验证）
- 支持 Windows / macOS / Linux Unity Editor（仅编辑器生效，不打包进运行时构建）

---

## License

MIT License © 2026 ZxYdzero
