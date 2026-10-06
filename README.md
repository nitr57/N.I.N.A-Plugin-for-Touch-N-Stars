# N.I.N.A-Plugin-for-Touch-N-Stars

[English version](README_en.md)

Dieses Plugin dient zur Integration von [Touch'N'Stars](https://github.com/Touch-N-Stars/Touch-N-Stars) in der Astrosoftware **NINA** (Nighttime Imaging 'N' Astronomy).

### 🚀 **Aktueller Status: Beta-Version**  
Das Plugin befindet sich aktuell in der Entwicklung und kann noch Fehler enthalten.

### 🔧 **Installieren**
Um das Plugin nutzen zu können, muss der Inhalt der ZIP-Datei in das Pluginverzeichnis von NINA kopiert werden.
Dies befindet sich meist unter: %LOCALAPPDATA%/NINA/Plugins/3.0.0/Touch-N-Stars
Sollte der Ordner nicht vorhanden sein, bitte vorher erstellen.

### 🧩 **Wichtige Hinweise** 
- Es wird das Plugin **Advanced API** in der aktuellsten Version benötigt.
  Der Port der API muss auf 1888 eingestellt sein und die V2 muss aktiv sein.
  Zusätzlich muss "Use Access-Control-Allow-Origin Header" aktiv sein.
- Für das Three Point Polar Alignment wird die Version 2.2.2.0 oder neuer benötigt.

### PHD2-KI-Guiding und PINS-Builds

Die [bestehende PHD2-API-Dokumentation](PHD2_API_README.md#native-ai-guiding)
beschreibt KI-Status, Modelle, Training und Betriebsmodi. Training und Vorhersage
laufen direkt in PHD2; Windows NINA und PINS verwenden dieselbe Plugin-API.
Linux-Builds gegen die Assemblies des Pi-Images und Simulatorprüfungen sind in
der [englischen README](README_en.md#phd2-ai-guiding-and-pins-builds) beschrieben.
