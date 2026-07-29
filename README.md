<img height="100" src="icon.png"/>

**VPULSE** is a powerful recording software built on Open Broadcaster Software (OBS), designed for gamers and content creators. Record, clip, and upload gameplay highlights effortlessly, with smart automation and deep game integration.

### ✂️ Clip Editor

![image](https://github.com/user-attachments/assets/beed0524-35f1-48be-9dd8-c2455959d2f9)

### 🔥 Highlights

![image](https://github.com/user-attachments/assets/481cc9fa-3efb-412d-b668-8be7d11b9851)


### ⚙️ Settings

![image](https://github.com/user-attachments/assets/de300431-1b63-4ed2-a022-110f8f828d1a)


---

## ✨ Features  
- **Auto-Start Recording**: Begin recording automatically when your game launches.  
- **Instant Clipping**: Save key moments with a hotkey.
- **Direct Upload**: Share clips to **[segra.tv](https://segra.tv)** instantly.  
- **Game Integration**: Tracks in-game stats (kills, deaths, assists) to auto-generate highlights, powered by AI.  
- **Lightweight & Fast**: Built on OBS for 4K with 144 FPS capture with minimal performance impact.  
- **Customizable Settings**: Adjust recording quality (NVENC/AMD VCE), hotkeys, storage paths, etc.

---

## Why "VPULSE"?  
**VPULSE** is built to help you **preserve those moments**: the chaotic fun with friends, the clutch plays, and the wins that deserve their own highlight reel.

> VPULSE is a fork of [Segra](https://github.com/Segergren/Segra) by @Segergren, rebranded and maintained by @verticalhost.

---

## 🛠 Installation
1. **Download**: Get `VPULSE-win-Setup.exe` from [[latest release](https://github.com/verticalhost/vpulse/releases/latest)].  
2. **Install**: Run the setup.  
3. **Configure**:  
   - Set recording directory and video quality.  
   - Assign hotkeys for clipping/uploading.  
   - Connect your segra.tv account.  

## 🔄 Uninstallation
1. Open `Windows Settings`
2. Go to `Apps` -> `Installed apps`
3. Search for `VPULSE`
4. Click `Uninstall`

## 🤝 Contributing  
See [CONTRIBUTING.md](CONTRIBUTING.md) for setup, dependencies, and dev workflow.
Help improve VPULSE by:  
- Report bugs or suggest features  
- Submit pull requests

---

## 📜 License  
VPULSE is **GPLv2 licensed**.  

---

## 🔐 Code Signing Policy
<table>
  <tr>
    <td><a href="https://signpath.org/" target="_blank"><img src="https://avatars.githubusercontent.com/u/34448643" height="30" alt="SignPath logo" /></a></td>
    <td>free code signing on Windows provided by <a href="https://signpath.io/" target="_blank">SignPath.io</a>, certificate by <a href="https://signpath.org/" target="_blank">SignPath Foundation</a></td>
  </tr>
</table>


**Team roles**

| Role      | Person |
|-----------|--------|
| Authors   | @verticalhost |
| Reviewers | @verticalhost |
| Approvers | @verticalhost |

**Privacy**

Recording, clipping and highlight generation run entirely on your machine and send nothing. VPULSE
makes three kinds of network request, and no data leaves your PC except where listed:

- **Game detection data** — downloads a public game list from `cdn.segra.tv` and the recorder
  runtime from `segra.tv`. Anonymous, read-only, no account involved.
- **Sign-in (optional)** — signing in with VPZONE sends you to `vpzone.tv` and stores a token on
  your PC, encrypted for your Windows account. VPULSE reads your username and whether your VPZ+
  membership is active. Covered by [VPZONE's privacy policy](https://vpzone.tv/privacy).
- **Publishing (optional, not yet enabled)** — publishing a clip uploads that file to Gamefolio.
  Nothing is uploaded unless you press the publish button.

Airplane mode in Settings disables every one of these.

## Star History

<a href="https://www.star-history.com/#verticalhost/vpulse&Date">
 <picture>
   <source media="(prefers-color-scheme: dark)" srcset="https://api.star-history.com/svg?repos=verticalhost/vpulse&type=Date&theme=dark" />
   <source media="(prefers-color-scheme: light)" srcset="https://api.star-history.com/svg?repos=verticalhost/vpulse&type=Date" />
   <img alt="Star History Chart" src="https://api.star-history.com/svg?repos=verticalhost/vpulse&type=Date" />
 </picture>
</a>

## Acknowledgments
- **[OBS Studio](https://obsproject.com)**: The backbone of VPULSE's recording engine.
- **[ObsKit.NET](https://github.com/Segergren/ObsKit.NET)**: The modern C#/OBS bridge that powers VPULSE's recording functionality.
- **[FFmpeg](https://github.com/FFmpeg/FFmpeg)**: for video and image encoding.  
