# Sessione Quest: 20261116_104824_wifi

- Collegamento: **Wi-Fi** (server http://192.168.1.5:8000)
- Durata: 5.7 min (dalle 10:48:51, orologio del PC)
- Orologio del visore - orologio del PC: +5.54 s (misurato con adb)
- Visore: Quest 2
- Richieste all'Edge: 0; mesh dalla cache: 0

## Mesh generate e accuratezza (dal server)

Richieste non riuscite: sphere_01 (error 504, dopo 301.2 s).

## Frame rate (VrApi, un campione al secondo)

- Obiettivo 72 fps; campioni: 313 s, di cui 2 di avvio dell'app (fino al primo secondo a regime), riportati a parte ed esclusi dal resto
- Avvio: fps 1, 74; frame scartati 62
- fps a regime: mediana 72 (59-73); secondi sotto il 90% dell'obiettivo: 1 (0%)
- fps nei 2 s dopo la prima mesh dall'Edge: –; dopo le altre dall'Edge: –; dopo quelle dalla cache: –; lontano dalle mesh: mediana 72 (59-73)
- Frame scartati (Stale) a regime: 23
- Tempo dell'app per frame (App): mediana 6.7 (1.0-7.8) ms
- Uso CPU / GPU: mediana 0.28 (0.16-0.39) / mediana 0.61 (0.24-0.74)
- Temperatura massima: 30 °C
