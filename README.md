# Nomi

Assistant de rédaction pour Outlook, avec une mascotte flottante et un modèle de langage qui tourne **entièrement sur l'ordinateur**. Une fois installé, Nomi n'a besoin ni d'Internet ni d'aucune dépendance : le moteur ([llama.cpp](https://github.com/ggml-org/llama.cpp)), le modèle (Qwen3‑4B‑Instruct‑2507) et le runtime Visual C++ sont dans le MSI.

## Utilisation

- **Clic sur Nomi** : la bulle s'ouvre sur le mail sélectionné dans Outlook et Nomi commence à rédiger la réponse. Choisissez un ton (Neutre, Formel, Chaleureux, Bref), ajoutez une consigne (« accepter jeudi », « refuser poliment »…), puis **Ouvrir dans Outlook** : la réponse s'ouvre avec le texte au-dessus de votre signature. Rien n'est jamais envoyé sans vous.
- **Zone de l'écran** (bouton de la bulle ou `Ctrl+Alt+R`) : surlignez à la souris le passage auquel répondre, n'importe où à l'écran. La zone reste surlignée, dans la couleur choisie, pendant que Nomi la lit (OCR de Windows, hors ligne) et rédige. Sur Outlook, la réponse peut être ouverte sur le mail sélectionné.
- **Suggestions** : une fois activées, Nomi prépare en arrière-plan une réponse aux 5 derniers mails reçus (nombre réglable, non lus seulement en option, enregistrement dans les Brouillons en option). Un badge sur la mascotte indique les suggestions prêtes.
- **Question** (`Ctrl+Alt+N`) : question libre au modèle local.
- **Mascotte** : déplaçable n'importe où ; poignée en bas à droite ou `Ctrl + molette` pour la taille ; relâchée contre un bord, elle s'y cache à moitié ; clic droit pour le menu ; elle s'endort quand on la laisse tranquille.

## Paramètres

Clic gauche sur l'icône de Nomi dans les icônes cachées de la barre des tâches (clic droit : menu). Chaque réglage s'applique en direct à la mascotte et à la bulle, affichée en aperçu : on peut la déplacer par son en-tête et la redimensionner par son coin. **Annuler** remet tout comme avant, **Enregistrer** conserve.

- Mascotte : affichage, taille, opacité, premier plan, animations, mise en veille, coins de l'écran
- Bulle : thème, couleur d'accent et de fond (nuancier ou couleur personnalisée), opacité, largeur, hauteur, taille du texte, coins arrondis, emplacement (à côté de Nomi ou position libre)
- Réponse assistée : couleur et intensité du surlignage
- Outlook : suggestions automatiques, nombre de mails, brouillons, signature, langue, ton par défaut
- Modèle : fichier `.gguf` (un autre modèle peut être choisi), longueur, créativité, cœurs utilisés, libération de la mémoire après inactivité, préchargement
- Général : lancement au démarrage de Windows, raccourcis clavier

Configuration : `%APPDATA%\Nomi\settings.ini`. Journaux : `%LOCALAPPDATA%\Nomi\` (`nomi.log`, `engine.log`).

## Compatibilité et performances

- Windows 10 et 11 x64, Outlook classique 2016, 2019, 2021 ou Microsoft 365. Le « nouvel Outlook » (olk.exe) n'expose pas d'API et n'est pas pris en charge.
- Processeur avec AVX (la bonne variante du moteur est choisie au lancement). Environ 2,5 Go de RAM pendant que le modèle est chargé ; il est libéré après 20 minutes sans utilisation (réglable).
- Sur un processeur portable 4 cœurs, une réponse demande de 20 à 60 secondes ; le texte s'affiche au fil de l'écriture. Le modèle est préchargé au démarrage pour éviter l'attente du premier chargement.
- Le serveur du modèle n'écoute que sur 127.0.0.1, sur un port aléatoire, protégé par une clé générée à chaque lancement, et s'arrête avec Nomi.
- Selon la configuration de sécurité d'Outlook, la lecture du contenu des mails par un programme externe peut afficher un avertissement si aucun antivirus à jour n'est détecté.

## Installation

- `Nomi.msi` (dans les [Releases](../../releases)) : installation par machine dans `C:\Program Files\Nomi`, raccourcis dans le menu Démarrer, lancement automatique à l'ouverture de session (désactivable par utilisateur).
- Silencieuse : `msiexec /i Nomi.msi /qn` (ajoutez `NOMI_NOLAUNCH=1` pour ne pas démarrer Nomi à la fin).
- Une nouvelle version remplace la précédente.

## Build

```bash
powershell -ExecutionPolicy Bypass -File build.ps1
```

Le script n'utilise que ce que Windows fournit (compilateur C# du .NET Framework, `msi.dll`, `makecab`) : ni Visual Studio, ni SDK .NET, ni WiX. Au premier build, il télécharge dans `vendor\` le moteur llama.cpp et le modèle, versions épinglées et vérifiées par SHA-256. Les builds suivants se font hors ligne. Résultat : `dist\Nomi.msi`.

## Structure

| Fichier | Rôle |
| --- | --- |
| `src/Program.cs` | Démarrage, instance unique, orchestration |
| `src/MascotView.cs`, `src/MascotWindow.cs` | Mascotte (redessinée depuis `assets/mascot/mascot.svg`, 12 états animés) et fenêtre flottante |
| `src/AssistantPanel.cs`, `src/ui/Panel.xaml` | La bulle |
| `src/SettingsWindow.cs`, `src/ui/Settings.xaml` | Paramètres avec aperçu en direct |
| `src/LlmEngine.cs` | Pilotage du serveur llama.cpp local |
| `src/OutlookBridge.cs` | Outlook par COM (mail sélectionné, derniers mails, création de réponses) |
| `src/Prompts.cs` | Prompts, formule d'appel, tutoiement ou vouvoiement, nettoyage des réponses |
| `src/ZoneSelector.cs`, `src/ScreenOcr.cs` | Sélection surlignée et OCR de Windows |
| `src/SuggestionService.cs` | Suggestions en arrière-plan |
| `installer/MsiBuilder.cs` | Générateur du MSI |

Licences des composants embarqués : voir `THIRD-PARTY.txt`.
