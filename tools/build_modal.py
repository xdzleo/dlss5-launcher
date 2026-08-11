"""One-shot edit: turn the side detail panel into a centered modal dialog.

Kept as a script (not done by hand) because it splices a large XAML block and must be
reproducible if the layout is regenerated.
"""
import pathlib

MODAL_HEAD = '''
        <!-- ===== modal do jogo: capa + opcoes do RenoDX ===== -->
        <Grid Panel.ZIndex="10" Visibility="{Binding IsDialogOpen, Converter={StaticResource BoolToVis}}">
            <Border Background="#B3070810">
                <Border.InputBindings>
                    <MouseBinding MouseAction="LeftClick" Command="{Binding CloseDialogCommand}"/>
                </Border.InputBindings>
            </Border>

            <Border MaxWidth="1020" MaxHeight="820" Margin="40" CornerRadius="16"
                    Background="{StaticResource PanelBrush}" BorderThickness="1"
                    BorderBrush="{StaticResource BorderStrongBrush}"
                    HorizontalAlignment="Center" VerticalAlignment="Center">
                <Border.Effect>
                    <DropShadowEffect BlurRadius="40" ShadowDepth="10" Opacity="0.65" Color="Black"/>
                </Border.Effect>

                <Grid Margin="26,24,26,20">
                    <Grid.RowDefinitions>
                        <RowDefinition Height="*"/>
                        <RowDefinition Height="Auto"/>
                    </Grid.RowDefinitions>
                    <Grid.ColumnDefinitions>
                        <ColumnDefinition Width="Auto"/>
                        <ColumnDefinition Width="*"/>
                    </Grid.ColumnDefinitions>

                    <!-- coluna da capa -->
                    <StackPanel Grid.Row="0" Grid.Column="0" Width="216" Margin="0,0,24,0">
                        <Border Height="324" CornerRadius="12" ClipToBounds="True"
                                Background="#14161B" BorderThickness="1"
                                BorderBrush="{StaticResource BorderBrush2}">
                            <Grid>
                                <TextBlock Text="{Binding Selected.Initials}" FontSize="52" FontWeight="Bold"
                                           Foreground="#3A414E" HorizontalAlignment="Center"
                                           VerticalAlignment="Center"/>
                                <Border Visibility="{Binding Selected.HasCover, Converter={StaticResource BoolToVis}}">
                                    <Border.Background>
                                        <ImageBrush ImageSource="{Binding Selected.CoverPath, Converter={StaticResource CoverImage}}"
                                                    Stretch="UniformToFill"/>
                                    </Border.Background>
                                </Border>
                                <Border VerticalAlignment="Top" HorizontalAlignment="Left" Margin="9"
                                        CornerRadius="7" Width="27" Height="27" Background="#B3080A0E"
                                        ToolTip="{Binding Selected.StoreLabel}">
                                    <Path Data="{Binding Selected.Game.Store, Converter={StaticResource StoreIcon}}"
                                          Fill="#E8EDF5" Stretch="Uniform" Width="14" Height="14"/>
                                </Border>
                            </Grid>
                        </Border>

                        <Button Margin="0,12,0,0" Padding="12,10" Command="{Binding LaunchGameCommand}"
                                AutomationProperties.Name="Jogar">
                            <StackPanel Orientation="Horizontal" HorizontalAlignment="Center">
                                <Path Data="{StaticResource IconPlay}" Fill="{StaticResource TextBrush}"
                                      Stretch="Uniform" Width="14" Height="14" VerticalAlignment="Center"/>
                                <TextBlock Text="Jogar" Margin="8,0,0,0" FontSize="12.5" FontWeight="SemiBold"
                                           VerticalAlignment="Center"/>
                            </StackPanel>
                        </Button>

                        <TextBlock Text="PASTA DE INSTALACAO" Style="{StaticResource SectionLabel}" Margin="0,16,0,5"/>
                        <TextBlock Text="{Binding Selected.Game.InstallDir}" Style="{StaticResource Caption}"
                                   FontSize="10.5" TextTrimming="CharacterEllipsis" MaxHeight="46"
                                   ToolTip="{Binding Selected.Game.InstallDir}"/>
                    </StackPanel>

                    <!-- coluna das opcoes -->
                    <Grid Grid.Row="0" Grid.Column="1">
                        <Grid.RowDefinitions>
                            <RowDefinition Height="Auto"/>
                            <RowDefinition Height="*"/>
                        </Grid.RowDefinitions>

                        <StackPanel Grid.Row="0" Margin="0,0,0,12">
                            <TextBlock Text="{Binding Selected.Name}" Style="{StaticResource H1}" TextWrapping="Wrap"/>
                            <StackPanel Orientation="Horizontal" Margin="0,7,0,0">
                                <TextBlock Text="{Binding Selected.StoreLabel}" FontSize="11.5"
                                           Foreground="{StaticResource TextDimBrush}" VerticalAlignment="Center"/>
                                <Border Width="1" Height="11" Background="{StaticResource BorderStrongBrush}" Margin="10,0"/>
                                <TextBlock Text="{Binding Selected.BadgeText}" FontSize="11.5" FontWeight="SemiBold"
                                           Foreground="{StaticResource AccentBrush}" VerticalAlignment="Center"/>
                            </StackPanel>
                        </StackPanel>

                        <ScrollViewer Grid.Row="1" VerticalScrollBarVisibility="Auto" Padding="0,0,10,0">
'''

MODAL_TAIL = '''
                        </ScrollViewer>
                    </Grid>

                    <!-- barra de acoes -->
                    <Border Grid.Row="1" Grid.Column="0" Grid.ColumnSpan="2" Margin="0,18,0,0" Padding="0,16,0,0"
                            BorderBrush="{StaticResource BorderBrush2}" BorderThickness="0,1,0,0">
                        <DockPanel>
                            <Button DockPanel.Dock="Right" Content="Fechar" MinWidth="104" Padding="14,8"
                                    Command="{Binding CloseDialogCommand}" AutomationProperties.Name="Fechar"/>
                            <StackPanel Orientation="Horizontal">
                                <Button Margin="0,0,8,0" Padding="11,8" Command="{Binding OpenFolderCommand}"
                                        ToolTip="Abrir a pasta do jogo" AutomationProperties.Name="Abrir pasta do jogo">
                                    <Path Data="{StaticResource IconFolder}" Fill="{StaticResource TextBrush}"
                                          Stretch="Uniform" Width="15" Height="15"/>
                                </Button>
                                <Button Margin="0,0,8,0" Padding="11,8" Command="{Binding OpenNexusCommand}"
                                        ToolTip="Pagina do mod na internet" AutomationProperties.Name="Pagina do mod na internet">
                                    <Path Data="{StaticResource IconGlobe}" Fill="{StaticResource TextBrush}"
                                          Stretch="Uniform" Width="15" Height="15"/>
                                </Button>
                                <Button Margin="0,0,8,0" Padding="11,8" Command="{Binding RemoveCommand}"
                                        ToolTip="Remover o mod" AutomationProperties.Name="Remover o mod">
                                    <Path Data="{StaticResource IconTrash}" Fill="{StaticResource TextBrush}"
                                          Stretch="Uniform" Width="15" Height="15"/>
                                </Button>
                            </StackPanel>
                        </DockPanel>
                    </Border>
                </Grid>
            </Border>
        </Grid>
'''


def main() -> None:
    src = pathlib.Path(__file__).resolve().parent.parent / "src"
    xaml = src / "MainWindow.xaml"
    s = xaml.read_text(encoding="utf-8")

    options = pathlib.Path("/tmp/detail_inner.xaml").read_text(encoding="utf-8")
    idx = options.find('<StackPanel Margin="20,4,18,20">')
    if idx >= 0:
        options = options[idx:]
    options = options.replace('<StackPanel Margin="20,4,18,20">', "<StackPanel>", 1).rstrip()

    modal = MODAL_HEAD + options + MODAL_TAIL

    if "IsDialogOpen" in s:
        print("modal já inserido; nada a fazer")
        return

    s = s.replace("    <DockPanel>", "    <Grid>\n    <DockPanel>", 1)
    s = s.replace("    </DockPanel>\n</Window>", "    </DockPanel>\n" + modal + "    </Grid>\n</Window>", 1)
    xaml.write_text(s, encoding="utf-8")
    print("modal inserido")


if __name__ == "__main__":
    main()
