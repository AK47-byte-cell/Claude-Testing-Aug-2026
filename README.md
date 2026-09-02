# CourrielPDF

Application Windows portable qui convertit des courriels en PDF par glisser-déposer.
Elle fonctionne entièrement en local : aucun courriel ni aucune donnée ne quitte l'ordinateur.

*(An English summary follows the French text.)*

## Télécharger et lancer

1. Ouvrez la page **Releases** du dépôt et téléchargez `CourrielPDF.exe` (version la plus récente).
2. Placez le fichier où vous voulez (Bureau, Documents, clé USB) et double-cliquez dessus. Aucune installation,
   aucun droit d'administrateur.
3. Au premier lancement, Windows peut afficher « Windows a protégé votre ordinateur » parce que l'application
   n'est pas signée numériquement. Cliquez sur **Informations complémentaires**, puis **Exécuter quand même**.

Prérequis : Windows 10 ou 11 (64 bits) avec Microsoft Edge, présent par défaut. Edge sert uniquement de moteur
de rendu, en arrière-plan et sans accès réseau; votre session Edge habituelle n'est pas touchée.

## Utilisation

**Fichiers de courriels.** Glissez un ou plusieurs fichiers `.msg` (Outlook) ou `.eml` dans la fenêtre, ou un
dossier complet (les sous-dossiers sont parcourus). Chaque courriel devient un PDF portant le même nom, enregistré
dans le même dossier que le fichier d'origine.

**Directement depuis Outlook.** Sélectionnez un ou plusieurs messages dans la liste d'Outlook (version classique)
et glissez-les dans la fenêtre. Comme ces messages n'existent pas sous forme de fichier, l'application vous demande
dans quel dossier enregistrer les PDF; le dernier dossier utilisé est proposé. Le bouton
**Convertir la sélection Outlook…** fait la même chose sans glisser-déposer : sélectionnez les messages dans Outlook
puis cliquez sur le bouton.

**Plusieurs courriels à la fois.** Les courriels sont traités l'un après l'autre; la liste indique pour chacun le
résultat (PDF créé, pièces jointes fusionnées ou enregistrées, avertissements, erreurs). Vous pouvez déposer
d'autres courriels pendant une conversion, ils sont mis en file d'attente. Un double-clic sur une ligne ouvre le PDF.

## Ce que contient le PDF

Le PDF reproduit le courriel tel qu'il s'affiche dans Outlook (mise en forme, tableaux, couleurs, images intégrées,
signatures), précédé d'un en-tête en français :

    De :               Jean Tremblay <jean.tremblay@exemple.com>
    Envoyé :           mardi 2 septembre 2026 14 h 35
    À :                Me Julie Gagnon <jgagnon@cabinet.ca>
    Cc :               ...
    Cci :              (seulement si présent dans le fichier)
    Objet :            ...
    Pièces jointes :   Rapport.pdf (245 Ko) — jointe ci-après
                       Photos.zip (3,1 Mo) — enregistrée dans le sous-dossier

Les **pièces jointes PDF** sont ajoutées à la suite du courriel, chacune précédée d'une page de séparation
(« Pièce jointe 1 de 2 — Rapport.pdf »). Les **courriels joints** (messages transférés en pièce jointe) sont
eux aussi convertis et ajoutés à la suite. Les **autres pièces jointes** (Word, Excel, images, etc.) sont enregistrées
dans leur format d'origine dans un sous-dossier `<nom du PDF> - pièces jointes`, à côté du PDF. Les pages sont
numérotées « Page x de y » et le format est Lettre (8,5 × 11 po).

Les images hébergées sur Internet ne sont pas téléchargées par défaut (un cadre « Image externe non téléchargée »
les remplace); l'option correspondante permet de les inclure. Les scripts et contenus actifs des courriels sont
toujours retirés.

## Options

| Option | Effet |
| --- | --- |
| Nom des fichiers PDF | `Objet.msg → Objet.pdf` (défaut), `2026-09-02 14h35 - Objet.pdf`, ou `2026-09-02 14h35 - Expéditeur - Objet.pdf`. Un numéro est ajouté en cas de doublon, rien n'est écrasé. |
| Joindre les pièces jointes PDF | Fusionne les PDF joints après le courriel. |
| Enregistrer les autres pièces jointes | Crée le sous-dossier `… - pièces jointes`. |
| Convertir aussi les courriels joints | Les messages transférés en pièce jointe sont convertis et ajoutés. |
| Page de séparation | Page titre avant chaque pièce jointe fusionnée. |
| Numéroter les pages | « Page x de y » au bas de chaque page. |
| Télécharger les images externes | Autorise Edge à charger les images hébergées sur Internet. |
| Indiquer le fichier source | Petite ligne sous l'en-tête : fichier d'origine et date de conversion. |
| Ouvrir le dossier une fois terminé | Ouvre l'Explorateur sur le dossier de destination. |

Les options sont mémorisées d'une session à l'autre (`%APPDATA%\CourrielPDF\parametres.txt`).

## Limites connues

- Le glisser-déposer direct depuis Outlook et le bouton « Convertir la sélection Outlook… » exigent la version
  **classique** d'Outlook pour Windows. Avec le « nouveau Outlook » ou Outlook sur le web, enregistrez d'abord les
  messages (glissez-les vers un dossier Windows), puis glissez les fichiers obtenus dans l'application.
- Les messages glissés depuis Outlook sont lus à partir de la sélection courante de la fenêtre principale d'Outlook.
- Les pièces jointes PDF protégées par mot de passe ne peuvent pas être fusionnées; elles sont enregistrées dans le
  sous-dossier et l'en-tête le mentionne.
- L'exécutable n'est pas signé numériquement (avertissement SmartScreen au premier lancement).

## Ligne de commande

Un outil en ligne de commande (`courrielpdf`, .NET 8) offre les mêmes conversions pour les traitements par lots
et les tests automatisés : `courrielpdf -o Dossier\Sortie -n date "C:\Courriels"`. Lancez `courrielpdf --help`
pour la liste des options.

## Compilation

- `dotnet build CourrielPDF.sln -c Release` compile tout (le SDK .NET 8 suffit, même sous Linux : l'application
  Windows est compilée contre les assemblages de référence de .NET Framework 4.8).
- `dotnet test tests/CourrielPDF.Core.Tests` exécute les tests; les tests de conversion complète s'exécutent
  lorsqu'un navigateur Chromium (Edge ou Chrome) est disponible.
- L'exécutable final `src/CourrielPDF.App/bin/Release/net48/CourrielPDF.exe` est autonome : ses dépendances y sont
  intégrées (Costura.Fody). Le flux GitHub Actions (`.github/workflows/build.yml`) compile et teste sous Linux
  (Chrome) et sous Windows (Edge), puis publie l'exécutable dans une Release.

Structure : `src/CourrielPDF.Core` (lecture des courriels, composition HTML, rendu Edge/Chromium, assemblage PDF),
`src/CourrielPDF.App` (interface Windows Forms), `src/CourrielPDF.Cli`, `tests/CourrielPDF.Core.Tests`.

## Composants tiers

MsgReader (MIT), MimeKit (MIT), PDFsharp (MIT), HtmlAgilityPack (MIT), RtfPipe (MIT), Costura.Fody (MIT) et la
police Liberation Sans (SIL Open Font License 1.1, texte dans `src/CourrielPDF.Core/Assets`). Le rendu HTML est
confié au Microsoft Edge installé sur le poste.

---

## English summary

CourrielPDF is a portable Windows application (single `CourrielPDF.exe`, no installer, no admin rights) that
converts e-mails to PDF by drag and drop. Drop `.msg` or `.eml` files or whole folders: each e-mail becomes a PDF
saved next to the source file. Drag messages straight from classic Outlook and the app asks for a destination
folder. PDF attachments are merged after the message (with separator pages), forwarded e-mails are converted and
appended, other attachments are saved in a `… - pièces jointes` folder beside the PDF. The header block is in
French (De, Envoyé, À, Cc, Cci, Objet, Pièces jointes), pages are Letter size and numbered, and everything runs
offline using the Microsoft Edge engine already present on Windows 10/11. Download `CourrielPDF.exe` from the
Releases page; on first launch accept the SmartScreen prompt (the executable is not code-signed).
