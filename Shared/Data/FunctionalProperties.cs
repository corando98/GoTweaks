using NLog;
using Shared.Enums;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.Foundation.Collections;

namespace Shared.Data
{
    public class FunctionalProperties
    {
        protected static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        protected readonly Dictionary<Function, FunctionalProperty> properties;

        public FunctionalProperties(params FunctionalProperty[] inProperties)
        {
            properties = new Dictionary<Function, FunctionalProperty>();
            foreach (var property in inProperties)
            {
                // A single null entry must NOT abort the whole registry build: if it did,
                // `properties` would be left null and EVERY subsequent pipe Get/Set would
                // NRE, silently killing all hardware apply (issue #104 on Legion Go S, where
                // one device-gated manager getter returned null). Skip nulls and keep going.
                if (property == null)
                {
                    Logger.Warn("Skipping null property in registry build");
                    continue;
                }
                if (!properties.ContainsKey(property.Function))
                {
                    properties.Add(property.Function, property);
                }
                else
                {
                    Logger.Warn($"Duplicated property {property.Function}");
                }
            }
        }

        /// <summary>
        /// Try to get a property by its function type.
        /// </summary>
        public bool TryGetProperty(Function function, out FunctionalProperty property)
        {
            lock (properties)
            {
                return properties.TryGetValue(function, out property);
            }
        }

        /// <summary>
        /// Add a property dynamically (thread-safe for background initialization).
        /// </summary>
        public void Add(FunctionalProperty property)
        {
            if (property == null)
            {
                Logger.Warn("Ignoring null property in dynamic Add");
                return;
            }
            lock (properties)
            {
                if (!properties.ContainsKey(property.Function))
                {
                    properties.Add(property.Function, property);
                    Logger.Debug($"Added property {property.Function} dynamically");
                }
                else
                {
                    Logger.Warn($"Property {property.Function} already exists, skipping");
                }
            }
        }

        /// <summary>
        /// Handles a message from Named Pipe (ValueSet format).
        /// Returns the response ValueSet to be sent back via pipe.
        /// </summary>
        public ValueSet HandlePipeMessage(ValueSet message)
        {
            if (!message.TryGetValue(nameof(Function), out object funcObj))
            {
                Logger.Warn("Pipe message missing Function");
                return new ValueSet
                {
                    { nameof(Command), (int)Command.Response },
                    { "Error", "Message missing Function" }
                };
            }

            var function = (Function)(int)funcObj;
            if (function == Function.None)
            {
                return new ValueSet
                {
                    { nameof(Function), (int)function },
                    { nameof(Command), (int)Command.Response },
                    { "Error", "Function is None" }
                };
            }

            FunctionalProperty property;
            lock (properties)
            {
                if (!properties.TryGetValue(function, out property))
                {
                    Logger.Warn($"Property {function} not found for pipe message");
                    // Return error response so client doesn't timeout waiting
                    return new ValueSet
                    {
                        { nameof(Function), (int)function },
                        { nameof(Command), (int)Command.Response },
                        { "Error", $"Property {function} not found" }
                    };
                }
            }

            if (!message.TryGetValue(nameof(Command), out object cmdObj))
            {
                Logger.Warn("Pipe message missing Command");
                return new ValueSet
                {
                    { nameof(Function), (int)function },
                    { nameof(Command), (int)Command.Response },
                    { "Error", "Message missing Command" }
                };
            }

            var command = (Command)(int)cmdObj;
            var response = new ValueSet
            {
                { nameof(Function), (int)function },
                { nameof(Command), (int)Command.Response }
            };

            switch (command)
            {
                case Command.Get:
                    response = property.AddValueSetContent(response);
                    break;
                case Command.Set:
                    property.SuppressRemoteSync = true;
                    try
                    {
                        if (message.TryGetValue(nameof(Content), out object content))
                        {
                            long updatedTime = 0;
                            if (message.TryGetValue(nameof(UpdatedTime), out object timeObj))
                            {
                                updatedTime = Convert.ToInt64(timeObj);
                            }
                            property.SetValue(content, updatedTime);
                        }
                    }
                    finally
                    {
                        property.SuppressRemoteSync = false;
                    }
                    // A property can accept the in-memory value yet still fail the actual
                    // hardware/WMI/native apply performed inside its NotifyPropertyChanged
                    // override. Consult the opt-in IHardwareApplyResult hook so that case is
                    // reported truthfully instead of as "Success". Additive wire metadata:
                    // the current widget ignores the Content string on Set responses, so
                    // this only changes what honest data is available (and what the helper
                    // logs), not behavior - until the widget starts rendering it.
                    if (property is IHardwareApplyResult hardwareResult && !hardwareResult.LastApplySucceeded)
                    {
                        string failReason = hardwareResult.LastApplyFailureReason ?? "The helper could not apply this value to hardware.";
                        Logger.Warn($"Set {function}: value accepted but hardware apply FAILED: {failReason}");
                        response.Add(nameof(Content), $"Failed: {failReason}");
                    }
                    else
                    {
                        response.Add(nameof(Content), "Success");
                    }
                    break;
                default:
                    Logger.Warn($"Unknown command in pipe message: {command}");
                    break;
            }

            return response;
        }
    }
}
