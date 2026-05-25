# 🌍 Globe Guesser VR

A virtual reality game inspired by GeoGuessr, built in Unity for the Meta Quest 3. Players are dropped into a real-world Google Street View location and must figure out where they are in the world by spinning an interactive globe and placing their best guess.

---

## 🎬 Demo

### Gameplay
[![Gameplay Video](https://img.shields.io/badge/Watch-Gameplay-red?style=for-the-badge&logo=youtube)](https://youtu.be/TzxTTBywjDo)

### Presentation
[![Presentation Video](https://img.shields.io/badge/Watch-Presentation-red?style=for-the-badge&logo=youtube)](https://youtu.be/aP6cD-1GdsQ)

---

## 🛠️ Built With

- **Unity** (XR Interaction Toolkit)
- **Meta Quest 3**
- **Google Maps Tile API** — live Street View panoramas and globe texture
- **C#**

---

## ⚙️ Setup

### Prerequisites

- Unity **2022.3 LTS** or later
- Meta Quest 3 headset
- A **Google Maps API key** with the *Map Tiles API* enabled

### Installation

1. **Clone the repository**
   ```bash
   git clone https://github.com/aidanbroder/Globe-Guesser-VR.git
   ```

2. **Open in Unity**
   - Open Unity Hub
   - Click **Add → Add project from disk**
   - Select the cloned folder

3. **Add your Google Maps API key**
   - In the Unity Hierarchy, select the `SkyboxLoader` GameObject
   - In the Inspector, paste your Google Maps API key into the **Api Key** field

4. **Connect your Meta Quest 3**
   - Enable **Developer Mode** on your headset via the Meta mobile app
   - Connect via USB-C or Air Link
   - In Unity go to **File → Build Settings → Android** and click **Switch Platform**
   - Click **Build and Run**

### Testing Without a Headset

The project includes the **XR Device Simulator** for testing in the Unity Editor without a headset.

- Go to **Edit → Project Settings → XR Plug-in Management → XR Interaction Toolkit**
- Enable **Use XR Device Simulator in scenes**
- Hit **Play** in the Unity Editor

---

## 🗺️ How It Works

1. A random real-world location is selected from a pool of 125k long/lat coordinates
2. The game fetches a live 360° Street View panorama from the **Google Maps Tile API** (32 tiles stitched into a 4096×2048 equirectangular texture)
3. The panorama is applied as a Unity skybox — the player is immersed inside it
4. The player examines their surroundings and spins the interactive globe to place a guess pin
5. Distance is calculated using the **Haversine formula** and converted to a score out of 1000 points
6. The game runs for 5 rounds — try to get the highest score

---

## 👥 Team

| Name | Contributions |
|---|---|
| **Aidan Broder** | Google Maps Tile API integration, guessing globe mechanic |
| **Alex Tung** | Help screen, UI polish, headset setup and connection |
| **Vincent Gallegos** | Google Maps API support, random location manager script |

---

## 📄 License

This project was created for a university course. Not for commercial use.
