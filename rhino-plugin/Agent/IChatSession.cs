using System.Collections.Generic;

namespace RhinoSketchingTool
{
    /// <summary>The provider-neutral contract consumed by both chat presentations.</summary>
    internal interface IChatSession
    {
        event System.Action<bool> TurnCompleted;
        Transcript Transcript { get; }
        string Model { get; }
        string Version { get; }
        bool MissingCli { get; set; }
        Account Account { get; }
        List<ModelChoice> Models { get; }
        string Mode { get; }
        bool SigningIn { get; }
        string Status { get; }
        void Warm();
        bool Send(string text, IReadOnlyList<ChatAttachment> attachments = null, string context = null);
        void Interrupt();
        void SetModel(string value);
        void SetMode(string value);
        void SetEffort(string value);
        void Answer(string requestId, string decision);
        void Reset();
        void Close();
        void SignIn();
        void SignOut();
        bool Poll(int budget = 400);
    }
}
