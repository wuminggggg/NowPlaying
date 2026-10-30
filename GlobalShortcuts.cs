using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;

public sealed class GlobalShortcuts : IDisposable {
    delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);
    HookProc keyboardProc, mouseProc;
    IntPtr keyboard, mouse;
    public readonly System.Collections.Concurrent.ConcurrentQueue<string> Events = new System.Collections.Concurrent.ConcurrentQueue<string>();
    public readonly Dictionary<string,string> Bindings = new Dictionary<string,string>();
    public bool Recording { get; set; }
    public bool Enabled = true;
    string held = "";
    [StructLayout(LayoutKind.Sequential)] struct MouseData {
        public int x,y; public uint mouseData, flags, time; public UIntPtr extra;
    }
    [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint thread);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr h, int n, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
    [DllImport("kernel32.dll", CharSet=CharSet.Auto)] static extern IntPtr GetModuleHandle(string name);
    public GlobalShortcuts() {
        keyboardProc = Keyboard; mouseProc = Mouse;
        IntPtr module = GetModuleHandle(null);
        keyboard = SetWindowsHookEx(13, keyboardProc, module, 0);
        mouse = SetWindowsHookEx(14, mouseProc, module, 0);
        if (keyboard == IntPtr.Zero || mouse == IntPtr.Zero) {
            Dispose(); throw new InvalidOperationException("无法注册全局快捷键");
        }
    }
    string Prefix() {
        string s="";
        if ((GetAsyncKeyState(0x11)&0x8000)!=0) s+="Ctrl+";
        if ((GetAsyncKeyState(0x12)&0x8000)!=0) s+="Alt+";
        if ((GetAsyncKeyState(0x10)&0x8000)!=0) s+="Shift+";
        if ((GetAsyncKeyState(0x5B)&0x8000)!=0 || (GetAsyncKeyState(0x5C)&0x8000)!=0) s+="Win+";
        return s;
    }
    bool Dispatch(string chord, bool allowBare) {
        if (Recording) {
            if (!allowBare && chord.IndexOf('+')<0) return false;
            Recording=false;
            Events.Enqueue("capture:"+chord);
            return true;
        }
        if (!Enabled) return false;
        string action;
        if (Bindings.TryGetValue(chord,out action)) {
            Events.Enqueue(action);
            return true;
        }
        return false;
    }
    IntPtr Keyboard(int n, IntPtr w, IntPtr l) {
        if(n>=0) {
            int msg=w.ToInt32(), key=Marshal.ReadInt32(l);
            if(msg==0x101 || msg==0x105) {
                bool consumed = held==key.ToString(); if(consumed) held="";
                if(consumed) return new IntPtr(1);
            }
            if(msg==0x100 || msg==0x104) {
                if(key==16 || key==17 || key==18 || (key>=160&&key<=165) || key==91 || key==92)
                    return CallNextHookEx(keyboard,n,w,l);
                if(Recording && key==27) {
                    Recording=false; Events.Enqueue("cancel"); return new IntPtr(1);
                }
                if(held==key.ToString()) return new IntPtr(1);
                bool bare=(key>=112&&key<=135)||(key>=176&&key<=179);
                if(Dispatch(Prefix()+((Keys)key).ToString(),bare)) {
                    held=key.ToString(); return new IntPtr(1);
                }
            }
        }
        return CallNextHookEx(keyboard,n,w,l);
    }
    readonly HashSet<int> swallowedMouseUps = new HashSet<int>();
    IntPtr Mouse(int n, IntPtr w, IntPtr l) {
        if(n>=0) {
            int msg=w.ToInt32(); string key=null; bool bare=false; int up=0;
            if(swallowedMouseUps.Remove(msg)) return new IntPtr(1);
            if(msg==0x201) {key="MouseLeft";up=0x202;}
            if(msg==0x204) {key="MouseRight";up=0x205;}
            if(msg==0x207) {key="MouseMiddle";bare=true;up=0x208;}
            if(msg==0x20B) {
                MouseData data=(MouseData)Marshal.PtrToStructure(l,typeof(MouseData));
                key=(data.mouseData>>16)==1?"MouseX1":"MouseX2";bare=true;up=0x20C;
            }
            if(msg==0x20A) {
                MouseData data=(MouseData)Marshal.PtrToStructure(l,typeof(MouseData));
                key=((short)(data.mouseData>>16))>0?"WheelUp":"WheelDown";
            }
            if(key!=null && Dispatch(Prefix()+key,bare)) {
                if(up!=0) swallowedMouseUps.Add(up);
                return new IntPtr(1);
            }
        }
        return CallNextHookEx(mouse,n,w,l);
    }
    public void Dispose() {
        if(keyboard!=IntPtr.Zero) UnhookWindowsHookEx(keyboard);
        if(mouse!=IntPtr.Zero) UnhookWindowsHookEx(mouse);
        keyboard=mouse=IntPtr.Zero;
    }
}
public static class CoverStream {
    [DllImport("shcore.dll", PreserveSig=false)]
    static extern void CreateStreamOverRandomAccessStream(
        [MarshalAs(UnmanagedType.IUnknown)] object randomStream,
        ref Guid iid,
        [MarshalAs(UnmanagedType.Interface)] out System.Runtime.InteropServices.ComTypes.IStream stream);
    public static byte[] Read(object source) {
        var iid = new Guid("0000000c-0000-0000-C000-000000000046");
        System.Runtime.InteropServices.ComTypes.IStream stream;
        CreateStreamOverRandomAccessStream(source,ref iid,out stream);
        try {
            System.Runtime.InteropServices.ComTypes.STATSTG stat;
            stream.Stat(out stat,1);
            if(stat.cbSize<=0 || stat.cbSize>20*1024*1024) throw new InvalidOperationException("封面大小无效");
            var bytes=new byte[(int)stat.cbSize];
            stream.Read(bytes,bytes.Length,IntPtr.Zero);
            return bytes;
        } finally { Marshal.ReleaseComObject(stream); }
    }
}
public static class FloatingWindowChrome {
 [DllImport("user32.dll",EntryPoint="GetWindowLong")] static extern int GetWindowLong32(IntPtr window,int index);
 [DllImport("user32.dll",EntryPoint="GetWindowLongPtr")] static extern IntPtr GetWindowLong64(IntPtr window,int index);
 [DllImport("user32.dll",EntryPoint="SetWindowLong")] static extern int SetWindowLong32(IntPtr window,int index,int value);
 [DllImport("user32.dll",EntryPoint="SetWindowLongPtr")] static extern IntPtr SetWindowLong64(IntPtr window,int index,IntPtr value);
 [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr window,IntPtr after,int x,int y,int width,int height,uint flags);
 [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr window,int attribute,ref int value,int size);
 static long Get(IntPtr window,int index){return IntPtr.Size==8?GetWindowLong64(window,index).ToInt64():GetWindowLong32(window,index);}
 static void Set(IntPtr window,int index,long value){
  if(IntPtr.Size==8)SetWindowLong64(window,index,new IntPtr(value));else SetWindowLong32(window,index,(int)value);
 }
 public static void Raise(IntPtr window,bool topmost){SetWindowPos(window,new IntPtr(topmost?-1:-2),0,0,0,0,0x0013);}
 public static long Style(IntPtr window){return Get(window,-16);}
 public static void Apply(IntPtr window){
  Set(window,-16,Get(window,-16)&~0x00CF0000L);
  Set(window,-20,(Get(window,-20)&~0x00040000L)|0x00000080L);
  SetWindowPos(window,IntPtr.Zero,0,0,0,0,0x0037);
  int disabled=1;DwmSetWindowAttribute(window,2,ref disabled,4);
  int noBorder=unchecked((int)0xFFFFFFFE);DwmSetWindowAttribute(window,34,ref noBorder,4);
 }
}
public static class StartupPaths {
 [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] static extern uint GetFinalPathNameByHandle(IntPtr handle,System.Text.StringBuilder path,uint length,uint flags);
 public static string ResolveFile(string path){
  using(var file=System.IO.File.OpenRead(path)){
   var result=new System.Text.StringBuilder(32768);
   if(GetFinalPathNameByHandle(file.SafeFileHandle.DangerousGetHandle(),result,32768,0)==0)
    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
   string final=result.ToString();
   if(final.StartsWith(@"\\?\UNC\"))return @"\\"+final.Substring(8);
   return final.StartsWith(@"\\?\")?final.Substring(4):final;
  }
 }
}