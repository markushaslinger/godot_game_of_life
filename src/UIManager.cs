using Godot;
using System;

public sealed partial class UIManager : HBoxContainer
{
	[Export]
	private GameOfLife _gameOfLife = default!;
	
	public void HandleGliderModeButtonPressed()
	{
		_gameOfLife.Configure(Mode.Glider);
	}
	
	public void HandleGosperGliderModeButtonPressed()
	{
		_gameOfLife.Configure(Mode.GosperGlider);
	}
	
	public void HandlePulsarModeButtonPressed()
	{
		_gameOfLife.Configure(Mode.Pulsar);
	}
	
	public void HandleRandomModeButtonPressed()
	{
		_gameOfLife.Configure(Mode.Random);
	}
}
