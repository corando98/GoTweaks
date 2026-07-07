using System;
using System.Collections.Generic;
using Shared.Enums;
using Windows.Data.Json;
using Windows.UI.Core;
using Windows.UI.Xaml.Controls;

namespace XboxGamingBar.Data
{
    /// <summary>
    /// Receives the helper's setup/environment health warnings (JSON array of
    /// { id, msg, action? }) pushed via Function.SetupWarnings. The owner page
    /// renders them in the setup-warning banner; this class only parses and
    /// dispatches.
    /// </summary>
    internal class SetupWarningsProperty : WidgetProperty<string>
    {
        internal sealed class Warning
        {
            public string Id;
            public string Message;
            public string Action;
        }

        private readonly Page owner;

        /// <summary>Invoked on the UI thread with the parsed warning list (possibly empty).</summary>
        public Action<List<Warning>> OnWarningsChanged { get; set; }

        public SetupWarningsProperty(Page inOwner)
            : base("", null, Function.SetupWarnings)
        {
            owner = inOwner;
        }

        private List<Warning> Parse()
        {
            var result = new List<Warning>();
            if (string.IsNullOrWhiteSpace(Value)) return result;
            try
            {
                var array = JsonArray.Parse(Value);
                foreach (var item in array)
                {
                    var obj = item.GetObject();
                    result.Add(new Warning
                    {
                        Id = obj.GetNamedString("id", ""),
                        Message = obj.GetNamedString("msg", ""),
                        Action = obj.GetNamedString("action", ""),
                    });
                }
            }
            catch (Exception)
            {
                // Malformed push — treat as all-clear rather than crash the widget.
                result.Clear();
            }
            return result;
        }

        protected override async void NotifyPropertyChanged(string propertyName = "")
        {
            base.NotifyPropertyChanged(propertyName);

            if (owner != null)
            {
                await owner.Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    OnWarningsChanged?.Invoke(Parse());
                });
            }
        }
    }
}
