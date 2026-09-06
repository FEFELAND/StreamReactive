# Third-Party Notices

This project incorporates material from the following third-party software, used under license.

## ImageFactory (sprite material / rendering technique)

The emote rendering in `EmoteHud.cs` reuses the sprite material shipped by ImageFactory
(the embedded `Resources/sprite.assetbundle` containing the "_Sprite" Renderer material)
and follows its proven rendering approach (a `SpriteRenderer` on the default layer, which
renders true-color and bloom-free in both the headset and Camera2's desktop mirror).

- Source: https://github.com/WentTheFox/ImageFactory
- Author: Auros Nexus / WentTheFox
- License: MIT

```
MIT License

Copyright (c) 2021 Auros Nexus

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```
