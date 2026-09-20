# 嵌入字体

这两套字体**编译进程序集**（csproj 里的 `<Resource Include="Fonts\*" />`），
不依赖工控机装了什么——换一台机器、换一版 Windows 镜像，字宽字距都一样，
数字列不会忽然对不齐。

| 文件 | 用途 | 字重 |
|---|---|---|
| `NotoSansSC-Regular.otf` | 中文正文、标签、按钮 | 400 |
| `IBMPlexMono-Regular.ttf` | 数据行 24 px | 400 |
| `IBMPlexMono-Medium.ttf` | 参数值 30 px | 500 |
| `IBMPlexMono-SemiBold.ttf` | 主测量值 54 px | 600 |

## 为什么中文只带一个字重

界面上中文只用 400（见 `docs/design/README.md` 的字号规范）。
唯一的粗体是顶栏品牌那三个拉丁字母 `RGX`，由 WPF 算法加粗即可——
为三个字母再背 8 MB 的中文粗体不值。
以后若真要中文粗体，把 `NotoSansSC-Bold.otf` 放进本目录即可，
csproj 是按通配符收的，不用改。

## 引用方式

`Themes/Typography.xaml` 里按 pack URI 引用，后面留着系统字体兜底：

```xml
<FontFamily x:Key="Font.Sans">pack://application:,,,/Fonts/#Noto Sans SC, Microsoft YaHei UI, Microsoft YaHei</FontFamily>
<FontFamily x:Key="Font.Mono">pack://application:,,,/Fonts/#IBM Plex Mono, Consolas, Cascadia Mono</FontFamily>
```

IBM Plex Mono 三个字重的**传统字体族名**各不相同（`IBM Plex Mono Medm` 等），
但**排版字体族名**都是 `IBM Plex Mono`，WPF 按后者归组，再按 `FontWeight` 选脸。
万一某个环境没按排版族名归组，最坏结果是 Medium / SemiBold 退化成算法加粗的
Regular——还是同一套字形，不会错字体。

## 授权

两套都是 SIL Open Font License 1.1，授权文本随字体一同分发
（`OFL-*.txt`，csproj 里按 `CopyToOutputDirectory` 复制到输出目录）。

- **IBM Plex Mono** © 2017 IBM Corp.，保留字体名 "Plex"。
  保留字体名意味着**不得改名后再分发**——本工程原样嵌入，未做任何修改。
- **Noto Sans SC** © Google Inc. 等，无保留字体名。

两套都允许嵌入商业软件并随软件分发，无需署名于界面上。
