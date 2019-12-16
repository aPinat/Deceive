﻿using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Deceive.Properties;
using YamlDotNet.RepresentationModel;

namespace Deceive
{
    internal static class StartupHandler
    {
        internal static string DeceiveTitle => "Deceive " + Resources.DeceiveVersion;

        [STAThread]
        private static void Main()
        {
            try
            {
                StartDeceive();
            }
            catch (Exception ex)
            {
                // Show some kind of message so that Deceive doesn't just disappear.
                MessageBox.Show(
                    "Deceive encountered an error and couldn't properly initialize itself. Please contact the creator through GitHub (https://github.com/molenzwiebel/deceive) or Discord.\n\n" + ex,
                    DeceiveTitle,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error,
                    MessageBoxDefaultButton.Button1
                );
            }
        }

        /**
         * Actual main function. Wrapped into a separate function so we can catch exceptions.
         */
        private static void StartDeceive()
        {
            // We are supposed to launch league, so if it's already running something is going wrong.
            if (Utils.IsClientRunning())
            {
                Utils.InitPathWithRunningLcu();

                var result = MessageBox.Show(
                    "League is currently running. In order to mask your online status, League needs to be started by Deceive. Do you want Deceive to stop League, so that it can restart it with the proper configuration?",
                    DeceiveTitle,
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question,
                    MessageBoxDefaultButton.Button1
                );

                if (result != DialogResult.Yes) return;
                Utils.KillClients();
                Thread.Sleep(2000); // Riot Client takes a while to die
            }

            // Step 0: Check for updates in the background.
            Utils.CheckForUpdates();

            // Step 1: Open a port for our proxy, so we can patch the port number into the system yaml.
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;

            // Step 2: Find original League system.yaml, patch our localhost proxy in, and save it somewhere.
            // At the same time, also parse the system.yaml to get the original chat server locations.
            var leagueSystemYamlPath = Utils.GetSystemYamlPath(Utils.GetLcuPath());
            if (leagueSystemYamlPath == null) // If this is null, it means we canceled something that required manual user input. Just exit.
                return;

            var leagueYamlContents = File.ReadAllText(leagueSystemYamlPath);

            // Load the stream
            var leagueYaml = new YamlStream();
            leagueYaml.Load(new StringReader(leagueYamlContents));

            leagueYamlContents = leagueYamlContents.Replace("allow_self_signed_cert: false", "allow_self_signed_cert: true");
            leagueYamlContents = leagueYamlContents.Replace("chat_port: 5223", "chat_port: " + port);
            leagueYamlContents = new Regex("chat_host: .*?\t?\n").Replace(leagueYamlContents, "chat_host: localhost\n");

            // Write this to the league install folder and not the AppData folder.
            // This is because league segfaults if you give it an override path with unicode characters,
            // such as some users with a special character in their Windows user name may have.
            // We put it in the Config folder since the new patcher will nuke any non-league files in the install root.
            var leaguePath = Utils.GetLcuPath();
            var yamlPath = Path.Combine(Path.GetDirectoryName(leaguePath), "Config", "deceive-system.yaml");
            File.WriteAllText(yamlPath, leagueYamlContents);

            // Step 3: Find the Riot Client.
            var riotClientPath = Utils.GetRiotClientPath();

            // If the riot client doesn't exist, the user is either severely outdated or has a bugged install.
            if (riotClientPath == null)
            {
                MessageBox.Show(
                    "Deceive was unable to find the path to the Riot Launcher. If you have League installed and it is working properly, please file a bug report through GitHub (https://github.com/molenzwiebel/deceive) or Discord.",
                    DeceiveTitle,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error,
                    MessageBoxDefaultButton.Button1
                );

                return;
            }

            // Step 4: Find the old config URL and start proxy web server (likely https://clientconfig.rpg.riotgames.com)
            var riotYamlContents = File.ReadAllText(Utils.GetSystemYamlPath(riotClientPath));
            var riotYaml = new YamlStream();
            riotYaml.Load(new StringReader(riotYamlContents));
            var root = riotYaml.Documents[0].RootNode;
            var oldConfigUrl = root["region_data"][root["default_region"].ToString()]["servers"]["client_config"]["client_config_url"].ToString();
            var proxyServerPort = ConfigProxy.StartConfigProxy(oldConfigUrl, port);

            // Step 5: Start the Riot Client and wait for a connect.
            var startArgs = new ProcessStartInfo
            {
                FileName = riotClientPath,
                Arguments = "--client-config-url=\"http://localhost:" + proxyServerPort + "\" --launch-product=league_of_legends --launch-patchline=live -- --system-yaml-override=\"" + yamlPath + "\""
            };
            Process.Start(startArgs);
            var incoming = listener.AcceptTcpClient();

            // Step 6: Connect sockets.
            var sslIncoming = new SslStream(incoming.GetStream());
            var cert = new X509Certificate2(Resources.certificates);
            sslIncoming.AuthenticateAsServer(cert);

            // Find the chat information of the original system.yaml for that region.
            // Should use chat server from clientconfig in the future
            // rnet-stable.chat.si.riotgames.com is the working, but still unused, global chat server
            var regionDetails = leagueYaml.Documents[0].RootNode["region_data"][Utils.GetServerRegion()]["servers"]["chat"];
            var chatHost = regionDetails["chat_host"].ToString();
            var chatPort = int.Parse(regionDetails["chat_port"].ToString());

            var outgoing = new TcpClient(chatHost, chatPort);
            var sslOutgoing = new SslStream(outgoing.GetStream());
            sslOutgoing.AuthenticateAsClient(chatHost);

            // Step 7: All sockets are now connected, start tray icon.
            var mainController = new MainController();
            mainController.StartThreads(sslIncoming, sslOutgoing);
            Application.EnableVisualStyles();
            Application.Run(mainController);
        }
    }
}
