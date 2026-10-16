# Petit explorateur — mascotte SVG

## Fichiers
- `mascot.svg` : tête seule, transparente, calques nommés.
- `demo.html` : démonstrateur avec 12 boutons, damier, slider 64–256 px et enchaînement auto.
- `agent-states.json` : description des états et règles de transition.
- `agent.js` : contrôleur d'état minimal à intégrer dans Electron/web.

## Lancer
Pour éviter les restrictions des modules ES en `file://`, démarrez un serveur statique dans ce dossier, par exemple :
`python -m http.server 8000`
Puis ouvrez `http://localhost:8000/demo.html`.

## Intégration
```js
import { AgentMascot } from "./agent.js";
const mascot = new AgentMascot(document.querySelector("#robot"));
mascot.setState("listen");
mascot.setVoiceLevel(0.6); // 0..1
mascot.setDragVector(0.7, -0.2); // directions normalisées
```

États : `idle`, `listen`, `think`, `speak`, `success`, `error`, `surprise`, `sleep`, `loading`, `greeting`, `drag`, `edge`.

## Notes
- Palette : crème `#E8DED0`, bleu nuit `#263342`, orange `#F5A623`, accent joue `#E98969`, métal chaud `#B5A797`.
- SVG sans dépendance de rendu externe. Les animations CSS sont désactivées lorsque `prefers-reduced-motion: reduce` est actif.
- Le prototype emploie des transformations et morphings CSS pour les poses principales. Pour une synchronisation audio précise, reliez `setVoiceLevel()` à votre analyseur audio et adaptez les keyframes de parole à vos événements de phonèmes/syllabes.
- La bulle de sommeil est stylisée comme une bulle cartoon, sans contenu explicite.
