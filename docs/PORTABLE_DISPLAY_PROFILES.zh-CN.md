# 可移植双屏显示配置

`VerticalWorld` 默认继续使用 RGDSplus 的双 `1024x768` 横向合成画布。开发版也可以通过环境变量描述不同分辨率、不同排列方式或带偏移的上下屏，不需要在 C# 中为每台设备写死坐标。

## 配置原则

游戏和下屏 UI 先在一套逻辑画布中绘制，再等比放入每块物理屏幕。宽高比不同时会自动居中留黑边，不会拉伸像素画；触摸坐标只接受实际内容区域，并反向映射到 UI 坐标。

默认逻辑画布为 `1024x768`。可通过以下变量修改：

```bash
export SDV_DUAL_REFERENCE_WIDTH=1024
export SDV_DUAL_REFERENCE_HEIGHT=768
```

建议初次适配时保留默认逻辑画布。确认菜单、触摸和字体正常后，再评估是否需要提高逻辑分辨率。

上下屏也可以使用不同的逻辑尺寸：

```bash
export SDV_DUAL_UPPER_LOGICAL_WIDTH=1920
export SDV_DUAL_UPPER_LOGICAL_HEIGHT=1080
export SDV_DUAL_LOWER_LOGICAL_WIDTH=1240
export SDV_DUAL_LOWER_LOGICAL_HEIGHT=1080
```

## 简单排列

左右排列且上下屏分辨率不同：

```bash
export SDV_DUAL_SCREEN_LAYOUT=horizontal
export SDV_DUAL_UPPER_WIDTH=1920
export SDV_DUAL_UPPER_HEIGHT=1080
export SDV_DUAL_LOWER_WIDTH=1280
export SDV_DUAL_LOWER_HEIGHT=720
export SDV_RGDS_PHYSICAL_UPPER=left
export DISPLAY_WIDTH=3200
export DISPLAY_HEIGHT=1080
```

上下排列：

```bash
export SDV_DUAL_SCREEN_LAYOUT=vertical
export SDV_DUAL_UPPER_WIDTH=1920
export SDV_DUAL_UPPER_HEIGHT=1080
export SDV_DUAL_LOWER_WIDTH=1280
export SDV_DUAL_LOWER_HEIGHT=720
export DISPLAY_WIDTH=1920
export DISPLAY_HEIGHT=1800
```

`DISPLAY_WIDTH` 和 `DISPLAY_HEIGHT` 仍由启动环境负责设置，必须覆盖两块输出的完整合成画布。

## 精确矩形

Gamescope、KDE 或设备固件可能让两块屏幕带偏移。此时直接指定 `x,y,width,height`：

```bash
export SDV_DUAL_UPPER_BOUNDS=0,0,1920,1080
export SDV_DUAL_LOWER_BOUNDS=1920,180,1280,720
export DISPLAY_WIDTH=3200
export DISPLAY_HEIGHT=1080
```

精确矩形优先于简单排列变量。坐标以合成窗口左上角为原点，允许上下屏尺寸不同，但宽高必须为正数。

## ArmadaOS 初次探测

先在桌面模式确认系统看到的输出名称、分辨率、位置和旋转，再把结果写入启动环境。Gaming Mode 和桌面模式可能使用不同的 Gamescope/KWin 布局，应以实际启动游戏的会话为准。

启动日志 `vertical-world.log` 会记录：

```text
upper=...
lower=...
upper-content=...
lower-content=...
reference=...
```

检查内容矩形是否落在对应物理屏幕内。若画面正确但触摸偏移，优先确认触摸设备报告的是下屏本地坐标还是合成画布坐标；当前映射器同时接受这两种形式。

### AYN Thor 游戏模式

Thor 面板的自然扫描方向是上屏 `1080x1920`、下屏 `1080x1240`，横向使用时对应 `1920x1080` 与 `1240x1080`。ArmadaOS 游戏模式将下屏作为独立 DRM 租赁输出，因此使用面板本地坐标：

```bash
export SDV_DUAL_OUTPUT_MODE=lease
export SDV_DUAL_UPPER_WIDTH=1920
export SDV_DUAL_UPPER_HEIGHT=1080
export SDV_DUAL_LOWER_WIDTH=1240
export SDV_DUAL_LOWER_HEIGHT=1080
export SDV_DUAL_UPPER_LOGICAL_WIDTH=1920
export SDV_DUAL_UPPER_LOGICAL_HEIGHT=1080
export SDV_DUAL_LOWER_LOGICAL_WIDTH=1240
export SDV_DUAL_LOWER_LOGICAL_HEIGHT=1080
```

租赁模式下，两块屏幕的原点都是各自的 `(0,0)`；下屏画面由独立传输层提交到 `/tmp/gamescope-lease.sock`，不把游戏主窗口伪装成一块超宽显示器。

这不表示下屏是独立游戏或普通“伴侣应用”。星露谷仍然只有一个进程和一份游戏状态：SMAPI 模组把连续世界的下半部分，或地图、背包等页面，渲染成第二路画面；ArmadaOS 原生进程只负责把这路画面提交给下屏 DRM 租赁，并把下屏触摸送回同一个游戏。

连续世界模式使用跨双屏相机死区：角色可以从上屏穿过物理接缝进入下屏，接近两块屏幕组合后的外缘时相机才继续滚动。切换到页面模式后，下屏改由地图、背包等 UI 占用，上屏恢复单屏跟随相机。

模组配置中的 `DeviceProfile` 默认为 `auto`。在 ArmadaOS 上检测到 AYN Thor 后，会自动启用上述 `1920x1080 + 1240x1080` 租赁配置，因此安装完成后不需要在 Steam 启动参数中重复填写这些变量。也可以在 `config.json` 中明确设置：

```json
{
  "DeviceProfile": "ayn-thor-armada"
}
```

## 当前边界

- 当前版本在宽高比不同时采用等比留黑边，不会扩大世界视野填满异形屏幕。
- 合成画布路径已经可配置；ArmadaOS DRM 租赁、帧传输、触摸回传和系统下屏服务恢复已有开发版实现，仍需 AYN Thor 实机验证时序、颜色格式和性能。
- RGDSplus 的 evdev 设备发现仍保留设备专用逻辑；其他掌机应优先使用 SDL/SMAPI 触摸输入，或后续增加通用设备匹配配置。

使用正版 Steam Linux 版和已经安装好的 SMAPI 编译时，可以显式传入安装目录，不需要把游戏 DLL 复制进仓库：

```bash
dotnet build dualscreen/VerticalWorld/VerticalWorld.csproj -c Release \
  -p:GamePath="/path/to/steamapps/common/Stardew Valley" \
  -p:SmapiPath="/path/to/steamapps/common/Stardew Valley"
```
