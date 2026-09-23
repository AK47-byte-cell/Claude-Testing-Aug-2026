using System;
using System.Collections.Generic;
using System.IO;
using CourrielPDF.Core;
using CourrielPDF.Core.Conversion;
using CourrielPDF.Core.Naming;

namespace CourrielPDF.Cli
{
    /// <summary>Command-line front end, mostly used for automated testing of the conversion pipeline.</summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            if (args.Length >= 2 && string.Equals(args[0], "exemples", StringComparison.OrdinalIgnoreCase))
                return Exemples.Generate(args[1]);

            var options = new ConversionOptions();
            string? outputDir = null;
            var verbose = false;
            var attachmentsOnly = false;
            var inputs = new List<string>();

            for (var i = 0; i < args.Length; i++)
            {
                var a = args[i];
                switch (a)
                {
                    case "-h": case "--help": case "/?":
                        Usage();
                        return 0;
                    case "-o": case "--sortie":
                        if (++i >= args.Length) return Usage("Dossier manquant après " + a);
                        outputDir = args[i];
                        break;
                    case "-n": case "--nom":
                        if (++i >= args.Length) return Usage("Valeur manquante après " + a);
                        switch (args[i].ToLowerInvariant())
                        {
                            case "source": options.Naming = NamingPattern.SameAsSource; break;
                            case "date": options.Naming = NamingPattern.DateSubject; break;
                            case "date-exp": case "date-expediteur": options.Naming = NamingPattern.DateSenderSubject; break;
                            default: return Usage("Modèle de nom inconnu : " + args[i]);
                        }
                        break;
                    case "--pj": case "--pieces-jointes": attachmentsOnly = true; break;
                    case "--avec-images-integrees": options.AttachmentsIncludeInlineImages = true; break;
                    case "--sans-fusion-pdf": options.AppendPdfAttachments = false; break;
                    case "--sans-pj": options.SaveOtherAttachments = false; break;
                    case "--sans-courriels-joints":
                        options.ConvertEmbeddedMessages = false;
                        options.AttachmentsExtractForwardedMessages = false;
                        break;
                    case "--sans-separateurs": options.SeparatorPages = false; break;
                    case "--sans-numeros": options.NumberPages = false; break;
                    case "--sans-provenance": options.IncludeProvenance = false; break;
                    case "--images-externes": options.AllowRemoteImages = true; break;
                    case "--navigateur":
                        if (++i >= args.Length) return Usage("Chemin manquant après " + a);
                        options.BrowserPath = args[i];
                        break;
                    case "--delai":
                        if (++i >= args.Length || !int.TryParse(args[i], out var secs)) return Usage("Délai invalide.");
                        options.RenderTimeout = TimeSpan.FromSeconds(secs);
                        break;
                    case "-v": case "--verbose": verbose = true; break;
                    default:
                        if (a.StartsWith("-")) return Usage("Option inconnue : " + a);
                        inputs.Add(a);
                        break;
                }
            }

            if (inputs.Count == 0) return Usage("Aucun fichier ou dossier indiqué.");
            if (verbose) options.Log = m => Console.Error.WriteLine("  · " + m);

            var files = InputExpander.Expand(inputs);
            if (files.Count == 0)
            {
                Console.Error.WriteLine("Aucun fichier .msg ou .eml trouvé.");
                return 1;
            }

            var failures = 0;
            var savedTotal = 0;
            using (var service = new ConversionService(options))
            {
                if (!attachmentsOnly)
                {
                    try
                    {
                        Console.WriteLine("Navigateur : " + service.ResolveBrowserPath());
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine(ex.Message);
                        return 1;
                    }
                }

                foreach (var f in files)
                {
                    ConversionResult r;
                    if (attachmentsOnly)
                    {
                        var destination = outputDir ?? Path.GetDirectoryName(Path.GetFullPath(f)) ?? Directory.GetCurrentDirectory();
                        r = service.ExtractAttachments(f, destination);
                        if (r.Success)
                        {
                            savedTotal += r.SavedAttachmentPaths.Count;
                            var skipped = r.SkippedInlineImages > 0 ? ", " + r.SkippedInlineImages + " image(s) intégrée(s) ignorée(s)" : string.Empty;
                            Console.WriteLine("OK      " + f + " : " + r.SavedAttachmentPaths.Count + " pièce(s) jointe(s) enregistrée(s)" + skipped + " (" + r.Duration.TotalSeconds.ToString("0.0") + " s)");
                            foreach (var p in r.SavedAttachmentPaths) Console.WriteLine("        + " + p);
                        }
                    }
                    else
                    {
                        r = service.ConvertFile(f, outputDir);
                        if (r.Success)
                        {
                            Console.WriteLine("OK      " + r.OutputPdfPath + "  (" + r.PageCount + (r.PageCount == 1 ? " page" : " pages") + ", " + r.Duration.TotalSeconds.ToString("0.0") + " s)");
                            foreach (var p in r.AppendedAttachments) Console.WriteLine("        + fusionnée : " + p);
                            foreach (var p in r.SavedAttachmentPaths) Console.WriteLine("        + enregistrée : " + p);
                        }
                    }

                    if (!r.Success)
                    {
                        failures++;
                        Console.WriteLine("ERREUR  " + f + " : " + r.Error);
                    }
                    foreach (var w in r.Warnings) Console.WriteLine("        ! " + w);
                }
            }

            Console.WriteLine();
            if (attachmentsOnly)
                Console.WriteLine((files.Count - failures) + " courriel(s) traité(s), " + savedTotal + " pièce(s) jointe(s) enregistrée(s), " + failures + " erreur(s).");
            else
                Console.WriteLine((files.Count - failures) + " courriel(s) converti(s), " + failures + " erreur(s).");
            return failures == 0 ? 0 : 1;
        }

        private static int Usage(string? error = null)
        {
            if (error != null) Console.Error.WriteLine("Erreur : " + error + Environment.NewLine);
            Console.Error.WriteLine(ProductInfo.Name + " " + ProductInfo.Version + " — conversion de courriels (.msg, .eml) en PDF");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Usage : courrielpdf [options] <fichier ou dossier> ...");
            Console.Error.WriteLine("        courrielpdf exemples <dossier>        Écrit quelques courriels d'exemple");
            Console.Error.WriteLine();
            Console.Error.WriteLine("  -o, --sortie <dossier>     Dossier de destination (défaut : dossier du courriel)");
            Console.Error.WriteLine("  -n, --nom <modèle>         source (défaut) | date | date-exp");
            Console.Error.WriteLine("      --pj                   Enregistrer seulement les pièces jointes, sans créer de PDF");
            Console.Error.WriteLine("      --avec-images-integrees  Avec --pj : enregistrer aussi les images intégrées au texte");
            Console.Error.WriteLine("      --sans-fusion-pdf      Ne pas joindre les pièces jointes PDF à la suite du courriel");
            Console.Error.WriteLine("      --sans-pj              Ne pas enregistrer les autres pièces jointes");
            Console.Error.WriteLine("      --sans-courriels-joints  Ne pas convertir (ni fouiller) les courriels joints");
            Console.Error.WriteLine("      --sans-separateurs     Pas de page de séparation avant chaque pièce jointe");
            Console.Error.WriteLine("      --sans-numeros         Pas de numéros de page");
            Console.Error.WriteLine("      --sans-provenance      Pas de mention du fichier source");
            Console.Error.WriteLine("      --images-externes      Télécharger les images hébergées sur Internet");
            Console.Error.WriteLine("      --navigateur <chemin>  Chemin de msedge.exe ou chrome.exe");
            Console.Error.WriteLine("      --delai <secondes>     Délai maximal de rendu (défaut 90)");
            Console.Error.WriteLine("  -v, --verbose              Journal détaillé");
            return error == null ? 0 : 2;
        }
    }
}
