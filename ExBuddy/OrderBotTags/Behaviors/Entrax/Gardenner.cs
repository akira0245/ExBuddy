﻿// ReSharper disable once CheckNamespace

namespace ExBuddy.OrderBotTags.Behaviors
{
    using Buddy.Coroutines;
    using Clio.XmlEngine;
    using ff14bot;
    using ff14bot.Enums;
    using ff14bot.Helpers;
    using ff14bot.Managers;
    using ff14bot.RemoteWindows;
    using System.Linq;
    using System.Threading.Tasks;

    [XmlElement("EtxGardenner")]
    public class EtxGardenner : ExProfileBehavior
    {
        private const int PostInteractDelay = 2300;

        [XmlAttribute("AlwaysWater")]
        public bool AlwaysWater { get; set; }

        public new void Log(string text, params object[] args) { Logger.Info(text, args); }

        protected override async Task<bool> Main()
        {
            var watering = GardenManager.Plants.Where(r => !Blacklist.Contains(r) && r.Distance2D(Core.Player) < 5).ToArray();
            foreach (var plant in watering)
                //Water it if it needs it or if we have fertilized it 5 or more times.
                if (AlwaysWater || GardenManager.NeedsWatering(plant))
                {
                    var result = GardenManager.GetCrop(plant);
                    if (result != null)
                    {
                        Log("Watering {0} {1:X}", result, plant.ObjectId);
                        plant.Interact();
                        if (!await Coroutine.Wait(5000, () => Talk.DialogOpen)) continue;
                        Talk.Next();
                        if (!await Coroutine.Wait(5000, () => SelectString.IsOpen)) continue;
                        if (!await Coroutine.Wait(5000, () => SelectString.LineCount > 0)) continue;
                        if (SelectString.LineCount == 4)
                        {
                            SelectString.ClickSlot(1);
                            await Coroutine.Sleep(PostInteractDelay);
                        }
                        else
                        {
                            Log("Plant is ready to be harvested");
                            SelectString.ClickSlot(1);
                        }
                    }
                    else
                    {
                        Log("GardenManager.GetCrop returned null {0:X}", plant.ObjectId);
                    }
                }
            var plants = GardenManager.Plants.Where(r => r.Distance2D(Core.Player) < 5).ToArray();
            foreach (var plant in plants)
            {
                var result = GardenManager.GetCrop(plant);
                if (result == null) continue;
                Log("Fertilizing {0} {1:X}", GardenManager.GetCrop(plant), plant.ObjectId);
                plant.Interact();
                if (!await Coroutine.Wait(5000, () => Talk.DialogOpen)) continue;
                Talk.Next();
                if (!await Coroutine.Wait(5000, () => SelectString.IsOpen)) continue;
                if (!await Coroutine.Wait(5000, () => SelectString.LineCount > 0)) continue;
                if (SelectString.LineCount == 4)
                {
                    SelectString.ClickSlot(0);
                    if (await Coroutine.Wait(2000, () => GardenManager.ReadyToFertilize))
                    {
                        if (GardenManager.Fertilize() != FertilizeResult.Success) continue;
                        Log("Plant with objectId {0:X} was fertilized", plant.ObjectId);
                        await Coroutine.Sleep(PostInteractDelay);
                    }
                    else
                    {
                        Log("Plant with objectId {0:X} not able to be fertilized, trying again later", plant.ObjectId);
                    }
                }
                else
                {
                    Log("Plant is ready to be harvested");
                    SelectString.ClickSlot(1);
                }
            }
            return isDone = true;
        }
    }
}