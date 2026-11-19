# Snapline 晾衣绳

Snapline Windows 中文定制版，可截图、编辑、钉图，并把常用图片挂在桌面的晾衣绳上。

## 使用

| 操作 | 默认快捷键 |
| --- | --- |
| 开始截图 | F1 |
| 显示或隐藏晾衣绳 | F2 |
| 截图选区直接原位钉图并上杆 | F3 |
| 将已复制的外部图片挂上晾衣绳 | F3 |
| 单独钉图 | Q，支持自定义 |
| 确认并复制截图 | Enter |
| 取消截图 | Esc |

框选时按住 Shift 可截正方形。选区内保留原始亮度，左上角显示像素尺寸。松开鼠标后可以移动选框、标注、保存或钉图。桌面贴图有浅蓝细边，支持拖动、滚轮缩放和 Ctrl＋滚轮调整透明度。

## 获取与运行

从仓库的 Releases 页面下载 Windows ZIP，完整解压后运行 Snapline.exe。程序依赖 .NET 8 Windows Desktop Runtime。

## 编译

在 Windows 上使用 .NET 8 SDK，从仓库根目录执行下面的命令。

```powershell
dotnet publish src/Snapline/Snapline.csproj -c Release --self-contained false -o out
```

## 来源与许可

本项目基于 [Snapline](https://github.com/mohabujubara/snapline) 的 Windows 版本。原工程部分代码源自 [Tendedero](https://github.com/alejandrobujan/tendedero)。源代码按 [MIT 许可证](LICENSE) 发布。随附 Manrope 字体遵循其目录中的 [OFL 许可证](src/Snapline/Assets/Fonts/OFL.txt)。
