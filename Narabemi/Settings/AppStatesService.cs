using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using CommunityToolkit.Diagnostics;
using Narabemi.UI.Windows;

namespace Narabemi.Settings
{
    /// <summary>
    /// load and save the AppState file.
    /// </summary>
    public class AppStatesService
    {
        private const string FileName = "appstates.json";

        private readonly JsonSerializerOptions _opt = new()
        {
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
            ReadCommentHandling = JsonCommentHandling.Skip,
            WriteIndented = true,
        };

        public AppStates? Current { get; private set; }

        public AppStatesService()
        {
            _opt.Converters.Add(new ColorJsonConverter());
        }

        public void LoadFile()
        {
            var jsonText = File.ReadAllText(FileName);
            Current = JsonSerializer.Deserialize<AppStates>(jsonText, _opt);
        }

        public void SaveFile()
        {
            Guard.IsNotNull(Current);

            var jsonText = JsonSerializer.Serialize(Current, _opt);
            File.WriteAllText(FileName, jsonText);
        }

        public void ApplyTo(MainWindowViewModel vm)
        {
            Guard.IsNotNull(Current);

            vm.Loop = Current.Loop;
            vm.AutoSync = Current.AutoSync;
            vm.MainPlayerIndex = Current.MainPlayerIndex;
            for (int i = 0; i < Math.Min(vm.PlayerViewModels.Count, Current.VideoPathList.Count); i++)
                vm.PlayerViewModels[i].VideoPath = Current.VideoPathList[i];
            vm.BlendBorderWidth = Current.BlendBorderWidth;
            vm.BlendBorderColor = Current.BlendBorderColor;
        }

        public void ApplyFrom(MainWindowViewModel vm)
        {
            Guard.IsNotNull(Current);

            Current.Loop = vm.Loop;
            Current.AutoSync = vm.AutoSync;
            Current.MainPlayerIndex = vm.MainPlayerIndex;
            Current.VideoPathList.Clear();
            Current.VideoPathList.AddRange(vm.PlayerViewModels.Select(v => v.VideoPath));
            Current.BlendBorderWidth = vm.BlendBorderWidth;
            Current.BlendBorderColor = vm.BlendBorderColor;
        }
    }
}
