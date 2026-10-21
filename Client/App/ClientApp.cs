namespace SysDVR.Client.App;

using ImGuiNET;
using SDL2;
using SysDVR.Client.Core;
using SysDVR.Client.GUI;
using SysDVR.Client.GUI.Components;
using SysDVR.Client.Platform;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;

public class ClientApp : IApplicationInstance
{
    public static string Version;

    public float UiScale { get; private set; }

    readonly CommandLineOptions CommandLine;

    SDLContext sdlCtx => Program.SdlCtx;

	public ClientApp(CommandLineOptions args)
    {
        CommandLine = args;
        ShowDebugInfo = Program.Options.Debug.GuiDebug;
	}

	// Fonts are loaded at double the resolution to reduce blurriness when scaling on high DPI
	const int FontMultiplier = 2;
	const int FontTextSize = 30 * FontMultiplier;
	const float FontH1Size = 45 * FontMultiplier;
	const float FontH2Size = 40 * FontMultiplier;
	
    public const float FontH1Scale = FontH1Size / FontTextSize;
    public const float FontH2Scale = FontH2Size / FontTextSize;
    public ImFontPtr FontText { get; private set; }

	public bool IsPortrait { get; private set; }

	// Special input state
	public bool ShiftDown { get; private set; }

	public event Action OnExit;

    // View state management
    Stack<View> Views = new();
    View? CurrentView = null;
    ImGuiStyle DefaultStyle;
    bool PendingViewChanges;
    FramerateCap Cap = new();

    // Async actions that must take place on the main thread outside of the rendering loop
    List<Action> PendingActions = new();

    // Debug window state
    public bool ShowDebugInfo = false;
    bool ShowImguiDemo;

    unsafe void BackupDefaultStyle()
    {
        DefaultStyle = *ImGui.GetStyle().NativePtr;
    }

    unsafe void RestoreDefaultStyle()
    {
        *ImGui.GetStyle().NativePtr = DefaultStyle;
    }

    // Private view API, has immediate effect
    void HandlePopView() 
    {
        PendingViewChanges = false;

        CurrentView?.LeaveForeground();
        CurrentView?.Destroy();
        CurrentView = null;
        if (Views.Count > 0 ) 
        {
            CurrentView = Views.Pop();
            Cap.SetMode(CurrentView.RenderMode);
            CurrentView?.ResolutionChanged();
            CurrentView?.EnterForeground();
        }
    }

    void HandleReplaceView(View v)
    {
        PendingViewChanges = false;

        CurrentView?.LeaveForeground();
        CurrentView?.Destroy();
        CurrentView = v;
        
        Cap.SetMode(CurrentView.RenderMode);
        CurrentView?.Created();
        CurrentView?.ResolutionChanged();
        CurrentView?.EnterForeground();
    }

    void HandlePushView(View v)
    {
        PendingViewChanges = false;

        if (CurrentView != null)
            Views.Push(CurrentView);

        CurrentView?.LeaveForeground();
        
        CurrentView = v;
        Cap.SetMode(CurrentView.RenderMode);
        CurrentView?.Created();
        CurrentView?.ResolutionChanged();
        CurrentView?.EnterForeground();
    }

    // Post an action to be handled in the main thread
    public void PostAction(Action act) 
    {
        lock (PendingActions)
        {
            PendingActions.Add(act);
        }
    }

    private void ExecutePendingActions() 
    {
        lock (PendingActions)
        {
            for (int i = 0; i < PendingActions.Count; i++)
                PendingActions[i]();
            PendingActions.Clear();
        }
    }

    // Public view management API, these are deferred as they can be called mid-drawing
    public void PushView(View view)
    {
        if (PendingViewChanges)
            throw new Exception("A view action is already scheduled");

        PendingViewChanges = true;
        PostAction(() => HandlePushView(view));
    }

    public void PopView()
    {
        if (PendingViewChanges)
            throw new Exception("A view action is already scheduled");

        PendingViewChanges = true;
        PostAction(HandlePopView);
    }

    public void ReplaceView(View view)
    {
        if (PendingViewChanges)
            throw new Exception("A view action is already scheduled");

        PendingViewChanges = true;
        PostAction(() => HandleReplaceView(view));
    }

    // Called to simulate input when rendering mode is adaptive, we need it so the video decoding thread can force a re-render even when there is no user input active
    public void KickRendering(bool important) 
    {
        Cap.OnEvent(important);
    }

    void DebugWindow()
    {
        ImGui.Begin("Info");
        ImGui.Text($"FPS: {ImGui.GetIO().Framerate} Cap {Cap.CapMode}");
		ImGui.Text(sdlCtx.GetDebugInfo());
		ImGui.Text($"scale: {UiScale} mode: {(IsPortrait ? "Portrait" : "Landscape")} stack: {Views.Count}");
		ImGui.Checkbox("Show imgui demo", ref ShowImguiDemo);
        CurrentView?.DrawDebug();
        ImGui.End();

        if (ShowImguiDemo)
            ImGui.ShowDemoWindow();
    }

    private void UpdateSize()
    {
        var w = sdlCtx.WindowSize.X;
        var h = sdlCtx.WindowSize.Y;

        IsPortrait = w / (float)h < 1.3;

        // Apply scale so the biggest dimension matches 1280
        var oldscale = UiScale;
        var biggest = Math.Max(w, h);
        UiScale = biggest / 1280f;
        if (UiScale != oldscale)
        {
            RestoreDefaultStyle();
            ImGui.GetStyle().ScaleAllSizes(UiScale);
            ImGui.GetIO().FontGlobalScale = UiScale / 2;
        }

        CurrentView?.ResolutionChanged();
    }

    unsafe void UnsafeImguiInitialization()
    {
        ImGui.GetIO().NativePtr->IniFilename = null;
    }

    public void Initialize() 
    {
        Program.DebugLog("Initializing app");

		ImGui.CreateContext();

        UnsafeImguiInitialization();

        ImGui.GetIO().ConfigFlags |= ImGuiConfigFlags.NavEnableKeyboard;
        ImGui.GetIO().NavVisible = true;
        ApplyImguiSettings();

        ApplyModernTheme();

        InitializeFonts();

        BackupDefaultStyle();
    }

    public static void ApplyModernTheme()
    {
        var style = ImGui.GetStyle();

        // Modern geometry & curvatures
        style.WindowRounding = 10.0f;
        style.ChildRounding = 8.0f;
        style.FrameRounding = 6.0f;
        style.PopupRounding = 8.0f;
        style.ScrollbarRounding = 10.0f;
        style.GrabRounding = 6.0f;
        style.TabRounding = 6.0f;

        style.WindowBorderSize = 1.0f;
        style.ChildBorderSize = 1.0f;
        style.PopupBorderSize = 1.0f;
        style.FrameBorderSize = 1.0f;
        style.TabBorderSize = 0.0f;

        style.WindowPadding = new Vector2(16.0f, 16.0f);
        style.FramePadding = new Vector2(10.0f, 7.0f);
        style.ItemSpacing = new Vector2(10.0f, 10.0f);
        style.ItemInnerSpacing = new Vector2(8.0f, 6.0f);
        style.ScrollbarSize = 14.0f;
        style.GrabMinSize = 12.0f;

        // Modern Sleek Dark Color Palette with Switch Neon-Blue Accents
        var colors = style.Colors;
        colors[(int)ImGuiCol.Text] = new Vector4(0.95f, 0.96f, 0.98f, 1.00f);
        colors[(int)ImGuiCol.TextDisabled] = new Vector4(0.52f, 0.56f, 0.63f, 1.00f);
        colors[(int)ImGuiCol.WindowBg] = new Vector4(0.08f, 0.09f, 0.12f, 1.00f);
        colors[(int)ImGuiCol.ChildBg] = new Vector4(0.11f, 0.12f, 0.16f, 1.00f);
        colors[(int)ImGuiCol.PopupBg] = new Vector4(0.11f, 0.13f, 0.17f, 0.98f);
        colors[(int)ImGuiCol.Border] = new Vector4(0.20f, 0.23f, 0.30f, 0.65f);
        colors[(int)ImGuiCol.BorderShadow] = new Vector4(0.00f, 0.00f, 0.00f, 0.00f);
        colors[(int)ImGuiCol.FrameBg] = new Vector4(0.14f, 0.16f, 0.22f, 1.00f);
        colors[(int)ImGuiCol.FrameBgHovered] = new Vector4(0.19f, 0.23f, 0.31f, 1.00f);
        colors[(int)ImGuiCol.FrameBgActive] = new Vector4(0.24f, 0.28f, 0.38f, 1.00f);
        colors[(int)ImGuiCol.TitleBg] = new Vector4(0.08f, 0.09f, 0.12f, 1.00f);
        colors[(int)ImGuiCol.TitleBgActive] = new Vector4(0.11f, 0.13f, 0.17f, 1.00f);
        colors[(int)ImGuiCol.TitleBgCollapsed] = new Vector4(0.06f, 0.07f, 0.09f, 0.75f);
        colors[(int)ImGuiCol.MenuBarBg] = new Vector4(0.11f, 0.12f, 0.16f, 1.00f);
        colors[(int)ImGuiCol.ScrollbarBg] = new Vector4(0.07f, 0.08f, 0.10f, 0.50f);
        colors[(int)ImGuiCol.ScrollbarGrab] = new Vector4(0.22f, 0.26f, 0.34f, 0.80f);
        colors[(int)ImGuiCol.ScrollbarGrabHovered] = new Vector4(0.30f, 0.36f, 0.46f, 1.00f);
        colors[(int)ImGuiCol.ScrollbarGrabActive] = new Vector4(0.38f, 0.45f, 0.58f, 1.00f);
        colors[(int)ImGuiCol.CheckMark] = new Vector4(0.20f, 0.60f, 1.00f, 1.00f);
        colors[(int)ImGuiCol.SliderGrab] = new Vector4(0.20f, 0.60f, 1.00f, 1.00f);
        colors[(int)ImGuiCol.SliderGrabActive] = new Vector4(0.35f, 0.70f, 1.00f, 1.00f);
        colors[(int)ImGuiCol.Button] = new Vector4(0.16f, 0.20f, 0.28f, 1.00f);
        colors[(int)ImGuiCol.ButtonHovered] = new Vector4(0.22f, 0.42f, 0.78f, 1.00f);
        colors[(int)ImGuiCol.ButtonActive] = new Vector4(0.18f, 0.36f, 0.68f, 1.00f);
        colors[(int)ImGuiCol.Header] = new Vector4(0.16f, 0.21f, 0.29f, 1.00f);
        colors[(int)ImGuiCol.HeaderHovered] = new Vector4(0.22f, 0.38f, 0.72f, 0.90f);
        colors[(int)ImGuiCol.HeaderActive] = new Vector4(0.26f, 0.44f, 0.82f, 1.00f);
        colors[(int)ImGuiCol.Separator] = new Vector4(0.20f, 0.23f, 0.30f, 0.70f);
        colors[(int)ImGuiCol.SeparatorHovered] = new Vector4(0.28f, 0.44f, 0.76f, 0.90f);
        colors[(int)ImGuiCol.SeparatorActive] = new Vector4(0.35f, 0.55f, 0.90f, 1.00f);
        colors[(int)ImGuiCol.ResizeGrip] = new Vector4(0.20f, 0.26f, 0.36f, 0.50f);
        colors[(int)ImGuiCol.ResizeGripHovered] = new Vector4(0.30f, 0.48f, 0.82f, 0.80f);
        colors[(int)ImGuiCol.ResizeGripActive] = new Vector4(0.38f, 0.58f, 0.95f, 1.00f);
        colors[(int)ImGuiCol.Tab] = new Vector4(0.13f, 0.16f, 0.22f, 1.00f);
        colors[(int)ImGuiCol.TabHovered] = new Vector4(0.22f, 0.42f, 0.78f, 0.90f);
        colors[(int)ImGuiCol.TabActive] = new Vector4(0.18f, 0.34f, 0.62f, 1.00f);
        colors[(int)ImGuiCol.TabUnfocused] = new Vector4(0.10f, 0.12f, 0.16f, 1.00f);
        colors[(int)ImGuiCol.TabUnfocusedActive] = new Vector4(0.14f, 0.19f, 0.26f, 1.00f);
        colors[(int)ImGuiCol.NavHighlight] = new Vector4(0.25f, 0.65f, 1.00f, 0.90f);
    }

	ImVector GetFontRanges()
    {
        var baseRange = Program.Strings.ImGuiGlyphRange switch
        {   
            StringTableMetadata.GlyphRange.ChineseSimplifiedCommon => ImGui.GetIO().Fonts.GetGlyphRangesChineseSimplifiedCommon(),
            StringTableMetadata.GlyphRange.Cyrillic => ImGui.GetIO().Fonts.GetGlyphRangesCyrillic(),
            StringTableMetadata.GlyphRange.Japanese => ImGui.GetIO().Fonts.GetGlyphRangesJapanese(),
            StringTableMetadata.GlyphRange.Korean => ImGui.GetIO().Fonts.GetGlyphRangesKorean(),
            StringTableMetadata.GlyphRange.Thai => ImGui.GetIO().Fonts.GetGlyphRangesThai(),
            StringTableMetadata.GlyphRange.Vietnamese => ImGui.GetIO().Fonts.GetGlyphRangesVietnamese(),
            // Default to this so all fants always have at least the ASCII range
            _ => ImGui.GetIO().Fonts.GetGlyphRangesDefault()
        };

		var ptr = ImFontGlyphRangesBuilderPtr.Create();

		if (baseRange != 0)
			ptr.AddRanges(baseRange);

		foreach (var s in Program.Strings.GetAllStringForFontBuilding())
			ptr.AddText(s);

		ptr.BuildRanges(out var imvec);
		ptr.Destroy();
		return imvec;
	}

	internal void InitializeFonts() 
    {
        ImGui.GetIO().Fonts.Flags |= ImFontAtlasFlags.NoPowerOfTwoHeight;
		var fontData = Resources.ReadResource(Resources.MainFont);
		
        unsafe
		{
            var fontRange = GetFontRanges();

			fixed (byte* fontPtr = fontData)
            {
				FontText = ImGui.GetIO().Fonts.AddFontFromMemoryTTF(fontPtr, fontData.Length, FontTextSize, null, fontRange.Data);

                // fontPtr and FontRange must be kept pinned until the atlases have actually been built or else bad things will happen
                ImGui.GetIO().Fonts.Build();
            }

            fontRange.Free();
		}
	}

    internal void PushMainview() 
    {
        // If no streaming has been requested boot into the main menu
        if (CommandLine.StreamingMode == CommandLineOptions.StreamMode.None)
            HandlePushView(new MainView(this));
        // If mode = network and no IP specified, connect to the first available console
        else if (CommandLine.StreamingMode == CommandLineOptions.StreamMode.Network && string.IsNullOrWhiteSpace(CommandLine.NetStreamHostname))
			HandlePushView(new NetworkScanView(this, Program.Options.Streaming, CommandLine.ConsoleSerial ?? "")); // ConsoleSerial is null when non specified, make it "" so the network view takes it as a 'cnnect to anything command'
        // If mode = network and IP specified, connect directly to that
		else if (CommandLine.StreamingMode == CommandLineOptions.StreamMode.Network)
			HandlePushView(new ConnectingView(this, DeviceInfo.ForIp(CommandLine.NetStreamHostname), Program.Options.Streaming));
		else if (CommandLine.StreamingMode == CommandLineOptions.StreamMode.Usb)
            HandlePushView(new UsbDevicesView(this, Program.Options.Streaming, CommandLine.ConsoleSerial ?? ""));
		else if (CommandLine.StreamingMode == CommandLineOptions.StreamMode.Stub)
			HandlePushView(new ConnectingView(this, DeviceInfo.Stub(), Program.Options.Streaming));
		else 
        {
            Debugger.Break();
        }
	}

    internal void ApplyImguiSettings() 
    {
        sdlCtx.AcceptControllerInput = Program.Options.ControllerInput;
        sdlCtx.DebugPrintSdlEvents = Program.Options.Debug.SDLEvents;

        if (Program.Options.ControllerInput)
            ImGui.GetIO().ConfigFlags |= ImGuiConfigFlags.NavEnableGamepad;
        else
            ImGui.GetIO().ConfigFlags &= ~ImGuiConfigFlags.NavEnableGamepad;

        ShowDebugInfo = Program.Options.Debug.GuiDebug;
    }

    public void Entrypoint()
    {
        sdlCtx.CreateWindow(CommandLine.WindowTitle);

        ImGuiSDL2Impl.InitForSDLRenderer(sdlCtx.WindowHandle, sdlCtx.RendererHandle);
        ImGuiSDL2Impl.Init(sdlCtx.RendererHandle);

        sdlCtx.UsingImgui = true;

        if (CommandLine.LaunchFullscreen)
            sdlCtx.SetFullScreen(true);

        UpdateSize();

        PushMainview();

        while (true)
        {
            ExecutePendingActions();

            if (CurrentView is null)
                break;

			GuiMessage msg = GuiMessage.None;
			while ((msg = sdlCtx.PumpEvents(out var evt)) != GuiMessage.None)
            {
                Cap.OnEvent(true);

                if (msg == GuiMessage.Resize)
                    UpdateSize();
                else if (msg == GuiMessage.BackButton)
                {
                    CurrentView.BackPressed();
                    // Don't pass this event to the imgui backend so it will not lose focus when we return to the previous page
                    continue;
                }
                else if (msg == GuiMessage.KeyDown && evt.key.keysym.scancode is SDL.SDL_Scancode.SDL_SCANCODE_LSHIFT or SDL.SDL_Scancode.SDL_SCANCODE_RSHIFT)
                    ShiftDown = true;
                else if (msg == GuiMessage.KeyUp && evt.key.keysym.scancode is SDL.SDL_Scancode.SDL_SCANCODE_LSHIFT or SDL.SDL_Scancode.SDL_SCANCODE_RSHIFT)
                    ShiftDown = false;
                else if (msg == GuiMessage.KeyUp)
                    CurrentView.OnKeyPressed(evt.key.keysym);
                else if (msg == GuiMessage.Quit)
                    goto break_main_loop;

				ImGuiSDL2Impl.ProcessEvent(in evt);

#if ANDROID_LIB
                if (ImGui.GetIO().WantTextInput)
					sdlCtx.StartMobileTextInput();
				else if (!ImGui.GetIO().WantTextInput)
                    sdlCtx.StopMobileTextInput();
#endif
            }

            if (Cap.Cap())
                continue;

            ImGuiSDL2Impl.Renderer_NewFrame();
            ImGuiSDL2Impl.SDL2_NewFrame();
            ImGui.NewFrame();

            CurrentView.Draw();

            if (ShowDebugInfo)
                DebugWindow();

            ImGui.Render();

            CurrentView.RawDraw();

            ImGuiSDL2Impl.RenderDrawData(ImGui.GetDrawData());

            sdlCtx.Render();
        }
    break_main_loop:

        while (CurrentView != null)
        {
            HandlePopView();
        }

        OnExit?.Invoke();

        ImGuiSDL2Impl.Renderer_Shutdown();
        ImGuiSDL2Impl.SDL2_Shutdown();
        // Seems to crash:
        //  ImGui.DestroyContext(ctx);

        sdlCtx.DestroyWindow();
    }
}