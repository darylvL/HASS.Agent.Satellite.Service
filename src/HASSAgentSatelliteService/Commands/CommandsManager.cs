﻿using HASSAgent.Shared.Enums;
using HASSAgent.Shared.Models.Config;
using HASSAgentSatelliteService.Settings;
using Serilog;

namespace HASSAgentSatelliteService.Commands
{
    /// <summary>
    /// Continuously performs command autodiscovery and state publishing
    /// </summary>
    internal static class CommandsManager
    {
        private static readonly Dictionary<CommandType, string> CommandInfo = new();

        private static bool _subscribed;

        private static bool _active = true;
        private static bool _pause;

        private static DateTime _lastAutoDiscoPublish = DateTime.MinValue;

        /// <summary>
        /// Initializes the command manager
        /// </summary>
        internal static async void Initialize()
        {
            // load command descriptions
            LoadCommandInfo();

            // wait while mqtt's connecting
            while (Variables.MqttManager.GetStatus() == MqttStatus.Connecting) await Task.Delay(250);

            // start background processing
            _ = Task.Run(Process);
        }

        /// <summary>
        /// Stop processing commands
        /// </summary>
        internal static void Stop() => _active = false;

        /// <summary>
        /// Pause processing commands
        /// </summary>
        internal static void Pause() => _pause = true;

        /// <summary>
        /// Resume processing commands
        /// </summary>
        internal static void Resume() => _pause = false;

        /// <summary>
        /// Unpublishes all commands
        /// </summary>
        /// <returns></returns>
        internal static async Task UnpublishAllCommands()
        {
            // unpublish the autodisco's
            if (!CommandsPresent()) return;

            var count = 0;
            foreach (var command in Variables.Commands)
            {
                await command.UnPublishAutoDiscoveryConfigAsync();
                await Variables.MqttManager.UnubscribeAsync(command);
                command.ClearAutoDiscoveryConfig();
                count++;
            }

            Log.Information("[COMMANDSMANAGER] Unpublished {count} command(s)", count);

            // reset last publish & subscribed
            _lastAutoDiscoPublish = DateTime.MinValue;
            _subscribed = false;
        }

        /// <summary>
        /// Generates new ID's for all commands
        /// </summary>
        internal static void ResetCommandIds()
        {
            if (!CommandsPresent()) return;
            foreach (var command in Variables.Commands) command.Id = Guid.NewGuid().ToString();

            StoredCommands.Store();
        }

        /// <summary>
        /// Continuously processes commands (autodiscovery, state)
        /// </summary>
        private static async void Process()
        {
            var firstRun = true;

            while (_active)
            {
                try
                {
                    if (firstRun)
                    {
                        // on the first run, just wait 1 sec - this is to make sure we're announcing ourselves,
                        // when there are no sensors or when the sensor manager's still initialising
                        await Task.Delay(TimeSpan.FromSeconds(1));
                    }
                    else await Task.Delay(TimeSpan.FromSeconds(30));

                    // are we paused?
                    if (_pause) continue;

                    // is mqtt available?
                    if (Variables.MqttManager.GetStatus() != MqttStatus.Connected)
                    {
                        // nothing to do
                        continue;
                    }

                    // we're starting the first real run
                    firstRun = false;

                    // do we have commands?
                    if (!CommandsPresent()) continue;

                    // publish availability & sensor autodisco's every 30 sec
                    if ((DateTime.Now - _lastAutoDiscoPublish).TotalSeconds > 30)
                    {
                        // let hass know we're still here
                        await Variables.MqttManager.AnnounceAvailabilityAsync();

                        // publish command autodisco's
                        foreach (var command in Variables.Commands.TakeWhile(_ => !_pause).TakeWhile(_ => _active))
                        {
                            if (_pause || Variables.MqttManager.GetStatus() != MqttStatus.Connected) continue;
                            await command.PublishAutoDiscoveryConfigAsync();
                        }

                        // are we subscribed?
                        if (!_subscribed)
                        {
                            foreach (var command in Variables.Commands.TakeWhile(_ => !_pause).TakeWhile(_ => _active))
                            {
                                if (_pause || Variables.MqttManager.GetStatus() != MqttStatus.Connected) continue;
                                await Variables.MqttManager.SubscribeAsync(command);
                            }
                            _subscribed = true;
                        }

                        // log moment
                        _lastAutoDiscoPublish = DateTime.Now;
                    }

                    // publish command states (they have their own time-based scheduling)
                    foreach (var command in Variables.Commands.TakeWhile(_ => !_pause).TakeWhile(_ => _active))
                    {
                        if (_pause || Variables.MqttManager.GetStatus() != MqttStatus.Connected) continue;
                        await command.PublishStateAsync();
                    }
                }
                catch (Exception ex)
                {
                    Log.Fatal(ex, "[COMMANDSMANAGER] Error while publishing: {err}", ex.Message);
                }
            }
        }

        /// <summary>
        /// Stores the provided commands, and (re)publishes them
        /// </summary>
        /// <param name="commands"></param>
        /// <param name="toBeDeletedCommands"></param>
        /// <returns></returns>
        internal static async Task<bool> StoreAsync(List<ConfiguredCommand> commands, List<ConfiguredCommand>? toBeDeletedCommands = null)
        {
            toBeDeletedCommands ??= new List<ConfiguredCommand>();

            try
            {
                // pause processing
                Pause();

                // process the to-be-removed
                if (toBeDeletedCommands.Any())
                {
                    foreach (var abstractCommand in toBeDeletedCommands.Select(StoredCommands.ConvertConfiguredToAbstract))
                    {
                        if (abstractCommand == null) continue;

                        // remove and unregister
                        await abstractCommand.UnPublishAutoDiscoveryConfigAsync();
                        await Variables.MqttManager.UnubscribeAsync(abstractCommand);
                        Variables.Commands.RemoveAt(Variables.Commands.FindIndex(x => x.Id == abstractCommand.Id));

                        Log.Information("[COMMANDS] Removed command: {command}", abstractCommand.Name);
                    }
                }

                // copy our list to the main one
                foreach (var abstractCommand in commands.Select(StoredCommands.ConvertConfiguredToAbstract))
                {
                    if (abstractCommand == null) continue;

                    if (Variables.Commands.All(x => x.Id != abstractCommand.Id))
                    {
                        // new, add and register
                        Variables.Commands.Add(abstractCommand);
                        await Variables.MqttManager.SubscribeAsync(abstractCommand);
                        await abstractCommand.PublishAutoDiscoveryConfigAsync();
                        await abstractCommand.PublishStateAsync(false);

                        Log.Information("[COMMANDS] Added command: {command}", abstractCommand.Name);
                        continue;
                    }

                    // existing, update and re-register
                    var currentCommandIndex = Variables.Commands.FindIndex(x => x.Id == abstractCommand.Id);
                    if (Variables.Commands[currentCommandIndex].Name != abstractCommand.Name)
                    {
                        // name changed, unregister and resubscribe on new mqtt channel
                        Log.Information("[COMMANDS] Command changed name, re-registering as new entity: {old} to {new}", Variables.Commands[currentCommandIndex].Name, abstractCommand.Name);

                        await Variables.Commands[currentCommandIndex].UnPublishAutoDiscoveryConfigAsync();
                        await Variables.MqttManager.UnubscribeAsync(Variables.Commands[currentCommandIndex]);
                        await Variables.MqttManager.SubscribeAsync(abstractCommand);
                    }

                    Variables.Commands[currentCommandIndex] = abstractCommand;
                    await abstractCommand.PublishAutoDiscoveryConfigAsync();
                    await abstractCommand.PublishStateAsync(false);

                    Log.Information("[COMMANDS] Modified command: {command}", abstractCommand.Name);
                }

                // annouce ourselves
                await Variables.MqttManager.AnnounceAvailabilityAsync();

                // store to file
                StoredCommands.Store();

                // done
                return true;
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "[COMMANDSMANAGER] Error while storing: {err}", ex.Message);
                return false;
            }
            finally
            {
                // resume processing
                Resume();
            }
        }

        /// <summary>
        /// Processes, stores and activates the received commands
        /// </summary>
        /// <param name="commands"></param>
        internal static void ProcessReceivedCommands(List<ConfiguredCommand> commands)
        {
            try
            {
                if (!commands.Any())
                {
                    Log.Warning("[COMMANDSMANAGER] Received empty list, nothing to do ..");
                    return;
                }

                // collect all current commands (to determine which are deleted)
                var currentCommands = Variables.Commands.Select(StoredCommands.ConvertAbstractToConfigured).ToList();

                // create the to-be-deleted list
                var toBeDeletedCommands = currentCommands.Where(command => command != null).Where(command => currentCommands.Any(x => x?.Id == command?.Id)).ToList();

                // log what we're doing
                Log.Information("[COMMANDSMANAGER] Processing {count} received command(s), deleting {del} command(s) ..", commands.Count, toBeDeletedCommands.Count);

                // process both lists
                _ = StoreAsync(commands, toBeDeletedCommands!);
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "[COMMANDSMANAGER] Error while processing received: {err}", ex.Message);
            }
        }

        private static bool CommandsPresent() => Variables.Commands.Any();

        /// <summary>
        /// Returns default information for the specified command type
        /// </summary>
        /// <param name="type"></param>
        /// <returns></returns>
        internal static string GetCommandDefaultInfo(CommandType type)
        {
            return !CommandInfo.ContainsKey(type) ? "Unknown command, make sure HASS.Agent has finished booting up." : CommandInfo[type];
        }

        /// <summary>
        /// Loads info regarding the various command types
        /// </summary>
        private static void LoadCommandInfo()
        {
            CommandInfo.Add(CommandType.ShutdownCommand, "Shuts down the machine after one minute.\r\n\r\nTip: accidentally triggered? Run 'shutdown /a' to abort.");
            CommandInfo.Add(CommandType.RestartCommand, "Restarts the machine after one minute.\r\n\r\nTip: accidentally triggered? Run 'shutdown /a' to abort.");
            CommandInfo.Add(CommandType.HibernateCommand, "Sets the machine in hibernation.");
            CommandInfo.Add(CommandType.SleepCommand, "Puts the machine to sleep.\r\n\r\nNote: due to a limitation in Windows, this only works if hibernation is disabled, otherwise it will just hibernate.\r\n\r\nYou can use something like NirCmd (http://www.nirsoft.net/utils/nircmd.html) to circumvent this.");
            CommandInfo.Add(CommandType.LogOffCommand, "Logs off the current session.");
            CommandInfo.Add(CommandType.LockCommand, "Locks the current session.");
            CommandInfo.Add(CommandType.CustomCommand, "Execute a custom command.\r\n\r\nThese commands run without special elevation. To run elevated, create a Scheduled Task, and use 'schtasks /Run /TN \"TaskName\"' as the command to execute your task.\r\n\r\nOr enable 'run as low integrity' for even stricter execution.");
            CommandInfo.Add(CommandType.PowershellCommand, "Execute a Powershell command or script.\r\n\r\nYou can either provide the location of a script (*.ps1), or a single-line command.\r\n\r\nThis will run without special elevation.");
            CommandInfo.Add(CommandType.MediaPlayPauseCommand, "Simulates 'media playpause' key.");
            CommandInfo.Add(CommandType.MediaNextCommand, "Simulates 'media next' key.");
            CommandInfo.Add(CommandType.MediaPreviousCommand, "Simulates 'media previous' key.");
            CommandInfo.Add(CommandType.MediaVolumeUpCommand, "Simulates 'volume up' key.");
            CommandInfo.Add(CommandType.MediaVolumeDownCommand, "Simulates 'volume down' key.");
            CommandInfo.Add(CommandType.MediaMuteCommand, "Simulates 'mute' key.");
            CommandInfo.Add(CommandType.KeyCommand, "Simulates a single keypress.\r\n\r\nYou can pick any of these values: https://docs.microsoft.com/en-us/windows/win32/inputdev/virtual-key-codes \r\n\r\nAnother option is using a tool like AutoHotKey and binding it to a CustomCommand.");
            CommandInfo.Add(CommandType.PublishAllSensorsCommand, "Resets all sensor checks, forcing all sensors to process and send their value.\r\n\r\nUseful for example if you want to force HASS.Agent to update all your sensors after a HA reboot.");
            CommandInfo.Add(CommandType.LaunchUrlCommand, "Launches the provided URL, by default in your default browser.\r\n\r\nTo use 'incognito', provide a specific browser in Configuration -> External Tools.");
            CommandInfo.Add(CommandType.CustomExecutorCommand, "Executes the command through the configured custom executor (in Configuration -> External Tools).\r\n\r\nYour command is provided as an argument 'as is', so you have to supply your own quotes etc. if necessary.");
        }
    }
}
