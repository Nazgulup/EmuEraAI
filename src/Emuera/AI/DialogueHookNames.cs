using System;

namespace MinorShift.Emuera.AI
{
    // Shared by desktop and Android; identifies text scopes, never executes a callback.
    internal static class DialogueHookNames
    {
        static readonly string[] Components={"KOJO","REVKOJO","MAEKOJO","EXTRAKOJO","ROTARKOJO","DUMMYKOJO"};
        internal static bool IsPreparation(string name)
        { return name.StartsWith("PRITRAIN_MESSAGE",StringComparison.OrdinalIgnoreCase) || name.StartsWith("PRETRAIN_MESSAGE",StringComparison.OrdinalIgnoreCase) || ContainsFamily(name,"PRITRAIN_MESSAGE") || ContainsFamily(name,"PRETRAIN_MESSAGE"); }
        internal static bool IsDialogue(string name)
        {
            return name.StartsWith("TRAIN_MESSAGE_COM",StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("MESSAGE_COM",StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("KOJO_",StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("M_KOJO",StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("AI_DIALOGUE",StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("SELF_KOJO",StringComparison.OrdinalIgnoreCase)
                || ContainsFamily(name,"TRAIN_MESSAGE_COM")
                || ContainsFamily(name,"TRAIN_MESSAGE_F_COM")
                || ContainsFamily(name,"TRAIN_MESSAGE_S_COM")
                || ContainsFamily(name,"MESSAGE_COM")
                // Observed callback components may be prefixed and numbered (K10_KOJO2).
                || ContainsKojoComponent(name);
        }
        static bool ContainsFamily(string name,string family)
        {
            int start=0;
            while(start<name.Length)
            {
                int at=name.IndexOf(family,start,StringComparison.OrdinalIgnoreCase); if(at<0) return false;
                int end=at+family.Length;
                if((at==0 || name[at-1]=='_') && (end==name.Length || name[end]=='_' || (name[end]>='0' && name[end]<='9'))) return true;
                start=at+1;
            }
            return false;
        }
        static bool ContainsKojoComponent(string name)
        {
            for(int start=0;start<name.Length;)
            {
                int end=name.IndexOf('_',start); if(end<0) end=name.Length;
                foreach(string component in Components)
                {
                    if(end-start<component.Length || string.Compare(name,start,component,0,component.Length,StringComparison.OrdinalIgnoreCase)!=0) continue;
                    bool numeric=true;
                    for(int i=start+component.Length;i<end;i++) if(name[i]<'0' || name[i]>'9') { numeric=false; break; }
                    if(numeric) return true;
                }
                start=end+1;
            }
            return false;
        }
    }
}
