using System;
using System.Collections.Generic;
using Rug.Osc;
using System.Threading;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Net;

// My Notes:
// adding an unknown address to the current dictionary seems incorrect. I need a way to differentate between a command
// and a varible/data being sent
// I think a secondary dictionary to save address and last known data would work.
// If the address is not linked to a certain command (and value combo) then the address's data is saved.
// Later, when the message is SENT, if the ADD/SUB option is chosen, then we add the adjustment to the last known value.

namespace VAOscPlugin
{

    public class VoiceAttackPlugin
    {
        public struct DataPair
        {
            public string command;
            public int value;

            public DataPair(string setCommand, int setValue)
            {
                command = setCommand;
                value = setValue;
            }
        }

        const string C_APP_NAME = "Lerk's Osc Plugin";
        const string C_APP_VERSION = "v0.2";

        public static Dictionary<string, string> commandDict = new Dictionary<string, string>();
        public static Dictionary<string, string> lastKnownValuesDict = new Dictionary<string, string>();

        private static OscReceiver _receiver;
        private static Task _receiverTask;
        private static CancellationTokenSource _cancelSource;

        private static int _senderPort = 0;
        private static IPAddress _senderIpAddress;

        public static string AssemblyDirectory
        {
            get
            {
                string fullPath = Assembly.GetAssembly(typeof(VoiceAttackPlugin)).Location;
                return Path.GetDirectoryName(fullPath);
            }
        }

        public static string VA_DisplayName()
        {
            return $"{C_APP_NAME} {C_APP_VERSION}";
        }

        public static string VA_DisplayInfo()
        {
            return $"{C_APP_NAME} allows VoiceAttack to send and receive OSC messages";
        }

        public static Guid VA_Id()
        {
            return new Guid("{874C92BC-1426-4EA6-9156-F24161796CB8}");
        }

        public static void VA_StopCommand()
        {

        }

        public static void VA_Init1(dynamic vaProxy)
        {
            string settingsPath = Path.Combine(AssemblyDirectory, "oscsettings.txt");
            vaProxy.WriteToLog("Loading mappings from: " + settingsPath, "black");



            string line;
            // Read the file and display it line by line.  
            using (StreamReader file = new StreamReader(settingsPath))
            {
                while ((line = file.ReadLine()) != null)
                {
                    if (!line.Contains(';')) continue;
                    var oscCommandSplit = line.Split(';');
                    var oscAddress = oscCommandSplit[0];
                    var vaCommand = line.Substring(oscCommandSplit[0].Length + 1);
                    vaProxy.WriteToLog("Mapping " + oscAddress + " to " + vaCommand);
                }
            };

            string portSettingPath = Path.Combine(AssemblyDirectory, "oscport.txt");
            int port = 0;
            using (StreamReader file = new StreamReader(portSettingPath))
            {
                string portString = file.ReadLine();
                port = int.Parse(portString);
            };

            // Create the receiver
            _receiver = new OscReceiver(port);
            _cancelSource = new CancellationTokenSource();
            _receiverTask = new Task(x =>
            {
                try
                {
                    while (_receiver.State != OscSocketState.Closed)
                    {
                        // if we are in a state to recieve
                        if (_receiver.State == OscSocketState.Connected)
                        {
                            // get the next message 
                            // this will block until one arrives or the socket is closed
                            OscPacket packet = _receiver.Receive();
                            var message = (OscMessage)packet;
                            String messageData = message.ToString().Split(' ')[1];



                            UpdateAdd(message.Address, messageData);

                            if (commandDict.ContainsKey(message.Address))
                            {
                                // retrieve the command from dictionary
                                vaProxy.Command.Execute(commandDict[message.Address]);
                            }

                            // add or update value in dictionary
                            if (!UpdateAdd(message.Address, messageData))
                                vaProxy.WriteToLog($"NEW Address: {message.Address} Value: {messageData}", "yellow");
                        }
                    }
                }
                catch (Exception ex)
                {
                    // if the socket was connected when this happens
                    // then tell the user
                    if (_receiver.State == OscSocketState.Connected)
                    {
                        vaProxy.WriteToLog("Exception in OSC listener loop: " + ex.ToString(), "red");
                    }
                }
            }, _cancelSource.Token, TaskCreationOptions.LongRunning);

            // Connect the receiver
            _receiver.Connect();

            // Start the listen thread
            _receiverTask.Start();


            string senderPath = Path.Combine(AssemblyDirectory, "oscsenderport.txt");
            using (StreamReader file = new StreamReader(senderPath))
            {
                string senderInfoString = file.ReadLine();
                var splitSenderInfo = senderInfoString.Split(':');
                _senderIpAddress = IPAddress.Parse(splitSenderInfo[0]);
                _senderPort = int.Parse(splitSenderInfo[1]);
            };

        }


        public static void VA_Exit1(dynamic vaProxy)
        {

            // close the Reciver 
            _receiver.Close();

            // Wait for the listen thread to exit
            _cancelSource.Cancel();

        }

        public static void VA_Invoke1(dynamic vaProxy)
        {

            try
            {
                string fullOscCommand = vaProxy.Context;
                string oscAddress = fullOscCommand.Contains(':') ? fullOscCommand.Split(':')[0] : fullOscCommand;
                string[] oscArgStrings = null;
                List<object> oscArgList = new List<object>();

                if (fullOscCommand.Contains(':'))
                {
                    oscArgStrings = fullOscCommand.Contains(';')
                        ? fullOscCommand.Split(':')[1].Split(';')
                        : (new string[] { fullOscCommand.Split(':')[1] });

                    foreach (string oscArgument in oscArgStrings)
                    {
                        if (oscArgument != null && (
                            oscArgument.StartsWith("i", StringComparison.OrdinalIgnoreCase) ||
                            oscArgument.StartsWith("f", StringComparison.OrdinalIgnoreCase) ||
                            oscArgument.StartsWith("b", StringComparison.OrdinalIgnoreCase) ||
                            oscArgument.StartsWith("s", StringComparison.OrdinalIgnoreCase)))
                        {

                            // look to see if the value has been previously stored
                            // parse the value as the correct data type
                            // if no previous value was found, use a default value 0
                            // should I add a config file for setting default values of parameters?

                            int intValue = 0;
                            float floatValue = 0;
                            bool boolValue = false;

                            int intCurrent = 0;
                            float floatCurrent = 0;
                            bool boolCurrent = false;

                            switch (oscArgument.Substring(0, 2))
                            {
                                case "i ":
                                    if (int.TryParse(oscArgument.Substring(2), out intValue))
                                    {
                                        UpdateAdd(oscAddress, intValue.ToString());
                                        oscArgList.Add(intValue);
                                    }
                                    break;

                                case "ia":
                                    if (lastKnownValuesDict.ContainsKey(oscAddress))
                                        int.TryParse(lastKnownValuesDict[oscAddress], out intCurrent);

                                    if (int.TryParse(oscArgument.Substring(2), out intValue))
                                    {
                                        intCurrent += intValue;
                                        UpdateAdd(oscAddress, intCurrent.ToString());
                                        oscArgList.Add(intCurrent);
                                    }
                                    break;

                                case "is":
                                    if (lastKnownValuesDict.ContainsKey(oscAddress))
                                        int.TryParse(lastKnownValuesDict[oscAddress], out intCurrent);

                                    if (int.TryParse(oscArgument.Substring(2), out intValue))
                                    {
                                        intCurrent -= intValue;
                                        UpdateAdd(oscAddress, intCurrent.ToString());
                                        oscArgList.Add(intCurrent);
                                    }
                                    break;

                                case "im":
                                    if (lastKnownValuesDict.ContainsKey(oscAddress))
                                        int.TryParse(lastKnownValuesDict[oscAddress], out intCurrent);

                                    if (int.TryParse(oscArgument.Substring(2), out intValue))
                                    {
                                        intCurrent *= intValue;
                                        UpdateAdd(oscAddress, intCurrent.ToString());
                                        oscArgList.Add(intCurrent);
                                    }
                                    break;

                                case "id":
                                    if (lastKnownValuesDict.ContainsKey(oscAddress))
                                        int.TryParse(lastKnownValuesDict[oscAddress], out intCurrent);

                                    if (int.TryParse(oscArgument.Substring(2), out intValue))
                                    {
                                        intCurrent /= intValue;
                                        UpdateAdd(oscAddress, intCurrent.ToString());
                                        oscArgList.Add(intCurrent);
                                    }
                                    break;

                                case "f ":
                                    if (float.TryParse(oscArgument.Substring(2).TrimEnd('f'), out floatValue))
                                    {
                                        UpdateAdd(oscAddress, floatValue.ToString());
                                        oscArgList.Add(floatValue);
                                    }
                                    break;

                                case "fa":
                                    if (lastKnownValuesDict.ContainsKey(oscAddress))
                                        float.TryParse(lastKnownValuesDict[oscAddress].TrimEnd('f'), out floatCurrent);

                                    if (float.TryParse(oscArgument.Substring(2), out floatValue))
                                    {
                                        floatCurrent += floatValue;
                                        UpdateAdd(oscAddress, floatCurrent.ToString());
                                        oscArgList.Add(floatCurrent);
                                    }
                                    break;

                                case "fs":
                                    if (lastKnownValuesDict.ContainsKey(oscAddress))
                                        float.TryParse(lastKnownValuesDict[oscAddress].TrimEnd('f'), out floatCurrent);

                                    if (float.TryParse(oscArgument.Substring(2), out floatValue))
                                    {
                                        floatCurrent -= floatValue;
                                        UpdateAdd(oscAddress, floatCurrent.ToString());
                                        oscArgList.Add(floatCurrent);
                                    }
                                    break;

                                case "fm":
                                    if (lastKnownValuesDict.ContainsKey(oscAddress))
                                        float.TryParse(lastKnownValuesDict[oscAddress].TrimEnd('f'), out floatCurrent);

                                    if (float.TryParse(oscArgument.Substring(2), out floatValue))
                                    {
                                        floatCurrent *= floatValue;
                                        UpdateAdd(oscAddress, floatCurrent.ToString());
                                        oscArgList.Add(floatCurrent);
                                    }
                                    break;

                                case "fd":
                                    if (lastKnownValuesDict.ContainsKey(oscAddress))
                                        float.TryParse(lastKnownValuesDict[oscAddress].TrimEnd('f'), out floatCurrent);

                                    if (float.TryParse(oscArgument.Substring(2), out floatValue))
                                    {
                                        floatCurrent /= floatValue;
                                        UpdateAdd(oscAddress, floatCurrent.ToString());
                                        oscArgList.Add(floatCurrent);
                                    }
                                    break;

                                case "b ":
                                    if (bool.TryParse(oscArgument.Substring(2), out boolValue))
                                    {
                                        UpdateAdd(oscAddress, boolValue.ToString());
                                        oscArgList.Add(boolValue);
                                    }
                                    break;

                                case "bn":
                                    if (lastKnownValuesDict.ContainsKey(oscAddress))
                                        bool.TryParse(lastKnownValuesDict[oscAddress], out boolCurrent);

                                    boolCurrent = !boolCurrent;
                                    UpdateAdd(oscAddress, boolCurrent.ToString());
                                    oscArgList.Add(boolCurrent);

                                    break;

                                case "s ":
                                    if (!string.IsNullOrEmpty(oscArgument.Substring(2)))
                                        oscArgList.Add(oscArgument.Substring(2));
                                    break;
                            }
                        }
                    }
                }

                using (OscSender sender = new OscSender(_senderIpAddress, 0, _senderPort))
                {
                    sender.Connect();
                    if (oscArgList.Any())
                    {
                        sender.Send(new OscMessage(oscAddress, oscArgList.ToArray()));
                    }
                    else
                    {
                        sender.Send(new OscMessage(oscAddress));
                    }
                }
            }
            catch (Exception ex)
            {
                vaProxy.WriteToLog("Error sending OSC message: " + ex.ToString(), "red");
            }


        }

        public static bool UpdateAdd(string inputAddress, string inputData)
        {
            if (lastKnownValuesDict.ContainsKey(inputAddress))
            {
                // update value
                lastKnownValuesDict[inputAddress] = inputData;
                return true;
            }
            else
            {
                // add new entry
                lastKnownValuesDict.Add(inputAddress, inputData);
                return false;
            }
        }
    }

}