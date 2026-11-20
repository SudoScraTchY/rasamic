using Microsoft.Extensions.Logging;

namespace RasaMic.Client;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		var builder = MauiApp.CreateBuilder();
		builder
			.UseMauiApp<App>()
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("IBMPlexSans-Regular.ttf", "PlexSans");
				fonts.AddFont("IBMPlexSans-Medium.ttf", "PlexSansMedium");
				fonts.AddFont("IBMPlexSans-SemiBold.ttf", "PlexSansSemibold");
				fonts.AddFont("IBMPlexMono-Regular.ttf", "PlexMono");
				fonts.AddFont("FluentSystemIcons-Regular.ttf", "FluentIcons");
			});

#if DEBUG
		builder.Logging.AddDebug();
#endif

		return builder.Build();
	}
}
