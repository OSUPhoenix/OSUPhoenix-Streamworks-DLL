using System.Collections.Generic;

namespace OSWTools.BotEliminator
{
    public class BotEliminatorData
    {
        public List<string> Twitch { get; set; } = new List<string>();
        public List<string> YouTube { get; set; } = new List<string>();
        public List<string> Kick { get; set; } = new List<string>();
    }
}
