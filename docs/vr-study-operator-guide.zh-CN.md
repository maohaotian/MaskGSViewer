# VR 实验调试与数据说明

本文档面向实验人员，用于调试和运行 `projects/Splatviewer_VR` 中的实验场景。默认场景是 `Assets/GSTestScene.unity`。

## 1. 模式与按键

系统有两种使用方式：

- 调试模式：普通 viewer 行为，用于检查高斯资产、位置、旋转、文件浏览器、预加载和轨迹。
- 实验模式：由 `UserStudyFlowController` 接管流程，用于输入 participant ID、按序呈现实验资产、切换 mask 条件和记录头部数据。

### 模式切换

在 Unity 中打开 `projects/Splatviewer_VR/Assets/GSTestScene.unity`，选中 `XRorigin`，找到 `UserStudyFlowController`：

- `Study Mode Enabled = true`：实验模式。
- `Study Mode Enabled = false`：调试模式。

推荐在停止 Play 后切换该选项。运行中切换时，不要只改 `Study Mode Enabled` 字段；如果需要从实验模式临时退回调试，请先禁用整个 `UserStudyFlowController` 组件，让它执行恢复逻辑，再调整配置。

实验模式下，`UserStudyFlowController` 默认会关闭或禁用普通 viewer 控制：

- `closeAndDisableFileBrowser`
- `disableSplatCycler`
- `disableLocomotion`
- `disableTrajectoryPlayerInput`
- `disableSplatRotator`

这些开关都在 `UserStudyFlowController` 的 `Study Ownership` 分组下。需要混合调试时，可以临时关闭对应开关。

### 实验模式按键

实验模式由实验负责人员在外部使用键盘控制流程。被试只佩戴头显观看，不需要在 VR 内自行调试。

| 操作 | 按键 |
| --- | --- |
| 输入 participant ID | 直接键盘输入 |
| 确认 ID / 开始实验 | `Enter` / 小键盘 `Enter` |
| 下一项资产 | `Right Arrow` |
| 上一项资产 | `Left Arrow` |
| 模式 1：照片屏幕 | `1` |
| 模式 2：内容 mask | `2` |
| 模式 3：前半球 mask | `3` |
| 模式 4：无 mask 360 | `4` |

确认 ID 和切换资产时会显示 loading 面板。loading 期间重复输入会被忽略，头部数据采样也会暂停。

### 调试模式键盘按键

调试模式同样由实验负责人员在外部使用键盘和鼠标操作，主要用于正式实验前检查高斯资产、位置、旋转、文件浏览器、预加载和轨迹。

| 功能 | 按键 |
| --- | --- |
| 移动 | `W A S D` |
| 上下移动 | `Space` / `C` |
| 加速移动 | `Shift` |
| 鼠标视角 | 左键或右键捕获鼠标后移动 |
| 释放鼠标 | `Esc` |
| 拖动相机 | 鼠标中键拖动 |
| 下一/上一高斯资产 | `R` / `F` |
| 旋转高斯资产 | `Q` / `E`，或方向键、`,`、`.` |
| 缩放高斯资产 | 鼠标滚轮 |
| 重置/翻转高斯资产 | `Home` / `End` |
| 打开/关闭文件浏览器 | `Esc` / `Tab` |
| 浏览器打开/加载 | `Enter` |
| 浏览器返回上级 | `Backspace` |
| 浏览器收藏 | `F` |
| 浏览器预加载 | `P` |
| movie mode 开始/停止 | `M` |
| movie 播放中调 FPS | `Left Arrow` / `Right Arrow` |
| 导入轨迹上一/下一视角 | `[` / `]` |
| 播放/暂停导入轨迹 | `T` |

## 2. 实验流程与高斯资产添加

### 标准实验流程

1. 打开 `Assets/GSTestScene.unity`。
2. 选中 `XRorigin`，确认 `UserStudyFlowController > Study Mode Enabled` 为 true。
3. 检查 `GS Asset Sequence (Drag Here)` 和 mask 参数。
4. 进入 Play。
5. 输入 participant ID，按 `Enter`。
6. 系统显示 loading，加载第一项资产。
7. 用 `1/2/3/4` 切换实验条件，用左右方向键切换资产。
8. 实验结束后正常退出 Play 或关闭程序，日志会写入 `HeadGazeLogs`。

### 四种实验模式

| 编号 | 名称 | 行为 |
| --- | --- | --- |
| 1 | `PhotoScreen` | 隐藏高斯资产，显示匹配照片。 |
| 2 | `MaskedContent` | 显示高斯资产，启用内容 mask，使用 `mode2InnerAngle` 和 `mode2OuterAngle`。 |
| 3 | `FrontHemisphereMask` | 显示高斯资产，前半球清晰，后半球进入 peripheral。 |
| 4 | `Unmasked360` | 显示高斯资产，关闭 mask。 |

### 添加高斯资产

推荐做法就是直接拖拽：

1. 将 `.ply`、`.spz`、`.sog` 或 `.spx` 文件放入 `projects/Splatviewer_VR/Assets/GaussianAssets/`。
2. 在 Unity 的 Project 窗口中找到这些高斯资产。
3. 选中场景里的 `XRorigin`。
4. 在 Inspector 里找到 `UserStudyFlowController > GS Asset Sequence (Drag Here)`。
5. 直接把高斯资产拖到这个列表里。列表从上到下就是实验播放顺序。

如果需要模式 1 的照片条件，把同名图片放在同一目录，例如 `korea_9999_unity_dx12.png`、`.jpg` 或 `.jpeg`。脚本会自动匹配同名图片。

相对文件名会先从 Inspector 里的 `Asset Folder` 查找。默认 `Asset Folder = GaussianAssets`，实际对应 `Assets/GaussianAssets`。

一般不需要手动填写路径。只有在需要自定义显示名称、照片路径或 build 版本路径配置时，才使用 `Detailed Asset List`。

如果 `GS Asset Sequence (Drag Here)` 没有配置，并且 `Auto Scan Asset Folder When Empty` 为 true，启动时会自动扫描 `Asset Folder` 下所有支持格式并按文件名排序加入实验列表。

### 文件放置与命名要求

默认统一放在：

```text
projects/Splatviewer_VR/Assets/GaussianAssets/
```

推荐每个资产使用同一个主文件名，文件名尽量只使用英文、数字、下划线或连字符，不使用空格和中文。例如：

```text
Assets/GaussianAssets/korea_9999_unity_dx12.ply
Assets/GaussianAssets/korea_9999_unity_dx12.json
Assets/GaussianAssets/korea_9999_unity_dx12.png
```

| 文件类型 | 放置位置 | 命名要求 |
| --- | --- | --- |
| 高斯资产 | `Assets/GaussianAssets/` | 支持 `.ply`、`.spz`、`.sog`、`.spx`。拖到 `GS Asset Sequence (Drag Here)` 的就是这个文件。 |
| 位姿 JSON | 与对应高斯资产同目录 | 推荐和高斯资产同名，例如 `korea_9999_unity_dx12.json`。 |
| 图片 | 与对应高斯资产同目录 | 推荐和高斯资产同名，支持 `.png`、`.jpg`、`.jpeg`，用于模式 1 照片屏幕。 |

位姿 JSON 自动匹配优先级：

1. 与高斯资产同名的 JSON，例如 `korea_9999_unity_dx12.ply` 对应 `korea_9999_unity_dx12.json`。
2. 同目录下的 `camera_trajectory_unity_dx12.json`。
3. 同目录下的 `camera_trajectory.json`。
4. 如果同目录下只有一个 `camera_trajectory*.json`，也会自动使用。

如果一个目录里放多个高斯资产，最推荐使用“高斯资产同名 JSON”的方式，避免多个资产误用同一个固定名称位姿文件。图片也按同名规则匹配；如果图片不同名，需要在 `Detailed Asset List` 里手动指定 `photoPath`。

### 资产播放顺序

当前实验固定按配置顺序播放，不按 participant ID 轮转或打乱：

- `GS Asset Sequence (Drag Here)` 有内容时，按这个列表从上到下播放。
- 如果拖拽列表为空并启用自动扫描，则按文件名排序播放。

如果某个资产指向的文件不存在，运行时会跳过该项并在 Console 打 warning。

## 3. Mask 脚本与参数

mask 主要由两个脚本配合：

- `UserStudyFlowController`：实验条件入口，负责在模式 2/3/4 中设置 mask 状态。
- `WorldFocusBlurController`：实际计算视野区域、模糊和周边替换效果。

### `UserStudyFlowController` 中的实验 mask 参数

| 参数 | 说明 |
| --- | --- |
| `mode2InnerAngle` | 模式 2 中保持完全清晰的半垂直 FOV，默认 20。数值越大，清晰区域越大。 |
| `mode2OuterAngle` | 模式 2 中达到完全 peripheral 的半垂直 FOV，默认 42。必须大于 `mode2InnerAngle`。 |
| `recaptureMaskForwardOnApply` | 切换资产或进入 mask 模式时，是否把当前头部朝向重新捕获为 mask 中心。默认 true。 |

模式 2 每次应用时会覆盖 `WorldFocusBlurController.innerAngle` 和 `outerAngle`。如果只在 `WorldFocusBlurController` 上手动改角度，但 `mode2InnerAngle/mode2OuterAngle` 不变，下一次切换模式或资产时会被实验脚本改回。

### `WorldFocusBlurController` 核心参数

| 参数 | 说明 |
| --- | --- |
| `effectEnabled` | 总开关。关闭后画面不做 mask 处理。 |
| `innerAngle` | 清晰区域边界。模式 2 会由 `mode2InnerAngle` 写入。 |
| `outerAngle` | peripheral 完全生效边界。模式 2 会由 `mode2OuterAngle` 写入。 |
| `useCameraAspectForFocus` | 使用相机宽高比生成矩形清晰窗口。 |
| `customFocusAspect` | 关闭上一项时使用的自定义宽高比。 |
| `worldAnchoredFocus` | 将 mask 投到固定世界平面上，头部平移会影响空间关系。 |
| `frontHemisphereOnly` | 前半球清晰、后半球 peripheral。模式 3 会启用此项。 |
| `focusPlaneDistance` | 固定 focus 平面距离，影响 world-anchored mask 的空间投影。 |
| `maxBlurPixels` | 周边最大模糊半径。 |
| `peripheralDim` | 周边额外变暗程度。 |
| `replacePeripheralWithSkybox` | 周边是否替换成 skybox，而不是模糊场景。 |
| `useRenderSettingsSkybox` | 使用 Unity RenderSettings.skybox 中的 cubemap。 |
| `skyboxCubemap` | 自定义周边 cubemap。为空时使用程序天空渐变。 |
| `skyboxZenithColor` / `skyboxHorizonColor` / `skyboxGroundColor` | 程序天空颜色。 |
| `skyboxExposure` | skybox 曝光。 |
| `captureOnEnable` | 组件启用时捕获当前相机姿态。 |
| `lockToCapturedDirection` | 是否把 mask 坐标固定到捕获时的世界方向。 |

### 调参建议

- 需要扩大中心清晰范围：增大 `mode2InnerAngle`。
- 需要让过渡更柔和：保持 `innerAngle` 不变，增大 `outerAngle`。
- 需要更强周边遮蔽：增大 `maxBlurPixels` 或 `peripheralDim`。
- 需要完全不显示周边内容：启用 `replacePeripheralWithSkybox`，并设置合适 skybox。
- 每个资产进入时都以当前朝向为中心：保持 `recaptureMaskForwardOnApply = true`。
- 需要固定整个实验的 mask 世界方向：关闭 `recaptureMaskForwardOnApply`，并在合适时机调用 `WorldFocusBlurController` 的 `Capture Current Focus Frame`。

## 4. 实验数据格式与脚本

### 输出目录

实验头部数据由 `HeadGazeDwellLogger` 写出。默认目录：

```text
projects/Splatviewer_VR/HeadGazeLogs/id-<participant_id>/session-<yyyyMMdd_HHmmss>/
```

示例：

```text
HeadGazeLogs/id-P001/session-20260709_153000/
```

生成文件：

- `*_summary.csv`：每个 participant / asset / mode 的汇总停留时间。
- `*_samples.csv`：按 `sampleInterval` 采样的逐点记录，默认 0.1 秒一条。

`HeadGazeLogs/` 已在 `.gitignore` 中忽略，不应纳入项目版本管理。程序正常退出、停止 recording、组件禁用或应用退出时会保存；如果 Unity 或程序直接崩溃，最后一段未 flush 的数据可能丢失。

### 记录暂停条件

`HeadGazeDwellLogger` 默认在以下情况下暂停采样：

- 文件浏览器打开。
- study 尚未开始，例如仍在 participant ID 输入界面。
- loading 面板显示中。

当 `requireActiveFocusEffect = true` 时，模式 1 和模式 4 因为 mask 关闭，区域分类通常会进入 `Unknown`。如果希望无 visible mask 时仍计算角度分类，可将该项设为 false 后再验证数据定义是否符合实验设计。

### `summary.csv` 字段

`summary.csv` 只记录每个资产和模式下的停留时间汇总，不包含逐点头部角度。头部转动角度见 `samples.csv`。

| 字段 | 含义 |
| --- | --- |
| `study_running` | 是否处于实验运行状态，1/0。 |
| `participant_id` | 被试 ID。 |
| `asset_position_1based` | 当前资产在实验列表中的位置，1-based。 |
| `asset_count` | 当前实验列表中的资产总数。 |
| `asset_index_0based` | 资产原始索引，0-based。 |
| `asset_label` | 资产标签。 |
| `splat_file` | 高斯资产文件名。 |
| `splat_path` | 高斯资产完整路径。 |
| `mode_number` | 模式编号，1-4。 |
| `mode_name` | 模式名称。 |
| `total_time_seconds` | 该上下文累计时间。 |
| `clear_time_seconds` | 清晰区域累计时间。 |
| `transition_time_seconds` | 过渡区域累计时间。 |
| `peripheral_time_seconds` | 周边区域累计时间。 |
| `unknown_time_seconds` | 无法分类或 mask 关闭时的累计时间。 |
| `clear_ratio` | 清晰区域占比。 |
| `transition_ratio` | 过渡区域占比。 |
| `peripheral_ratio` | 周边区域占比。 |
| `unknown_ratio` | unknown 占比。 |

### `samples.csv` 字段

`samples.csv` 包含 summary 的上下文字段，并额外记录每个采样点的头部姿态和角度：

| 字段 | 含义 |
| --- | --- |
| `elapsed_seconds` | 从当前记录参考时间开始的经过时间。 |
| `total_seconds` | logger 累计实验时间。 |
| `region` | `Clear`、`Transition`、`Peripheral` 或 `Unknown`。 |
| `focus_t` | mask 评估值，接近 0 表示清晰，接近 1 表示 peripheral。 |
| `pos_x/y/z` | 相机世界坐标。 |
| `rot_x/y/z/w` | 相机世界旋转四元数。 |
| `euler_x/y/z` | 相机世界旋转欧拉角，即头部在世界坐标下的转动角度。 |
| `relative_rot_x/y/z/w` | 相对实验开始后第一帧采样姿态的旋转四元数。 |
| `relative_euler_x/y/z` | 相对实验开始后第一帧头部姿态的转动角度。 |
| `horizontal_angle` | 相对 mask 中心的水平角。 |
| `vertical_angle` | 相对 mask 中心的垂直角。 |
| `angular_distance` | 相对 mask 中心的角距离。 |

### 相关脚本

| 脚本 | 作用 |
| --- | --- |
| `UserStudyFlowController.cs` | 实验主流程、participant ID、资产顺序、模式切换、loading 面板、照片屏幕。 |
| `HeadGazeDwellLogger.cs` | 头部姿态和 mask 区域数据记录，写出 summary/sample CSV。 |
| `WorldFocusBlurController.cs` | mask 区域评估、周边模糊、skybox 替换和方向捕获。 |
| `RuntimeSplatLoader.cs` | 运行时加载 `.ply/.spz/.sog/.spx` 并生成 GaussianSplatAsset。 |
| `VRFileBrowser.cs` | 调试模式下的键盘文件浏览、预加载和 movie mode 入口。 |
| `SplatCycler.cs` | 调试模式下按目录切换高斯资产、预加载和 movie mode 播放。 |
| `SplatRotator.cs` | 调试模式下旋转、缩放、翻转和重置高斯资产。 |
| `VRLocomotion.cs` | 调试模式下键盘/鼠标移动与视角控制。 |
| `CameraTrajectoryPlayer.cs` | 导入和播放相机轨迹。 |

### 数据检查建议

每次实验结束后先检查：

1. 是否生成了 `HeadGazeLogs/id-<ID>/session-<时间>/`。
2. 每个预期资产/mode 是否有对应 summary。
3. `participant_id` 是否正确。
4. `mode_number` 和 `asset_label` 是否和实验记录表一致。
5. 如果 `unknown_ratio` 很高，确认当时是否处于模式 1/4、mask 是否关闭，或 `WorldFocusBlurController` 是否被禁用。

## 5. 注意事项

### 数据记录测试

`HeadGazeDwellLogger` 的数据记录功能目前还没有经过充分测试。正式实验前必须先做小规模预实验，确认日志生成、字段含义、采样频率和模式切换后的记录行为都符合实验设计。

### 资产尺度检查与调节

高斯训练结果本身没有真实米制尺度，不同资产加载后可能过大或过小。正式实验前建议逐个资产检查观看尺度，避免被试看见的场景过近、过远或需要异常幅度转头。

调节流程：

1. 将 `UserStudyFlowController > Study Mode Enabled` 设为 false，进入调试模式。
2. 进入 Play，用 `R` / `F` 切换到要检查的高斯资产。
3. 用鼠标滚轮缩放高斯资产，直到头显内观看大小合适。
4. 选中场景里的 `GaussianSplat` 对象，记录它的 `Transform > Scale` 数值。通常三个轴保持相同数值。
5. 停止 Play 后，把记录的 Scale 数值手动填回 `GaussianSplat > Transform > Scale`。Unity 的 Play 模式修改不会自动保存，必须停止后再写回场景。
6. 重新进入 Play 复查一次，确认资产大小、初始观看位置和模式切换都正常。

如果不同资产需要不同尺度，当前直接拖拽列表只保存播放顺序，不会自动为每个资产保存独立 Scale。正式实验前需要把每个资产的合适 Scale 记录在实验配置表中，并在切换正式资产配置时复核 `GaussianSplat` 的 Scale 是否正确。
