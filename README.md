# ShowDesk
- 这是使用C#编写的简单托盘菜单程序，只需双击Esc即可隐藏、显示桌面图标。

## 功能

- 双击 `ESC` 隐藏/显示桌面图标
- 托盘图标支持
- 开机自动启动
- 兼容 Wallpaper Engine、TranslucentTB等桌面美化软件
- 后台静默运行
- 低内存占用

## 为什么使用 C# 而不是 AutoHotkey？

本项目灵感来源于 AutoHotkey 版本的 DeskHider：https://github.com/iandiv/DeskHider

后来重写为 C# 版本，主要原因：

- 相比 AHK 打包程序，C# WinForms是非常标准的 Windows 桌面程序形式。更不容易被安全软件误报造成误删


## 使用方式

当鼠标焦点位于桌面区域时：

- 双击 `ESC`
- 即可隐藏/显示桌面图标

或者：

- 使用托盘菜单进行切换

## 开机启动

可在托盘菜单中开启：

- 开机自动启动

程序在 Windows 开机启动时，会自动隐藏桌面图标。

## 编译

- .NET 8 SDK


