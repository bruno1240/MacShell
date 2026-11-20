# MacShell

Visual inspirado no macOS para o Windows 11 — leve, nativo e feito do zero em C# (.NET 8 + WPF + Direct3D 11).

## O que faz

**Dock**
- Ícones com efeito de lupa ao passar o mouse, nome do app em balão e ponto embaixo dos apps abertos.
- Ícone "pula" enquanto o app abre; clique traz a janela para frente (ou minimiza, se já estiver em foco).
- Arraste atalhos, programas, pastas ou arquivos para fixar; arraste os ícones para reordenar.
- Menu do botão direito: janelas abertas, Manter/Remover do Dock, Mostrar no Explorador, Encerrar.
- Esconde sozinho quando uma janela maximizada (ou encostada) cobre a área dele e volta ao encostar o mouse na borda de baixo.

**Barra superior**
- Translúcida (acrílico), com o app em foco, menus de app e de janela, busca, configurações rápidas e relógio.
- Botão ⌃ que abre os ícones ocultos (Steam, Discord, OneDrive...) logo abaixo dele, no topo da tela.
- Some apenas em tela cheia de verdade (jogos, vídeos, F11).

**Avisos nunca escondidos**
- Programa piscando pedindo atenção ou pedido de administrador pendente: a barra de tarefas aparece, o dock sobe acima dela e o ícone do app fica pulando até o pedido ser atendido.

**Animação gênio**
- Ao minimizar, a janela escorre para dentro do ícone do dock; ao restaurar pelo dock, sai dele.
- Renderizada na GPU (Direct3D 11 + DirectComposition): textura com mipmaps e filtro anisotrópico, malha deformável, bordas suavizadas, sincronizada com o monitor.

**Área de trabalho limpa**
- Barra de tarefas do Windows só aparece com a tecla Windows (Iniciar/busca).
- Ícones da área de trabalho escondidos.
- Nada é alterado nas configurações do Windows: tudo volta ao normal ao sair do MacShell.

## Desempenho

- 0% de CPU parado (tudo por eventos, sem timers rodando à toa).
- ~110–130 MB de memória; a memória de vídeo da animação é liberada entre usos.

## Compilar

Requer o [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```bash
dotnet build -c Release
```

O executável fica em `bin/Release/net8.0-windows/MacShell.exe`. Ao abrir, ele se coloca na inicialização do Windows (chave `HKCU\...\Run`).

## Uso

- Botão direito numa área vazia do dock: editar apps fixados, mostrar/esconder barra de tarefas e ícones da área de trabalho, sair.
- Configuração dos apps fixados: `%APPDATA%\MacShell\dock.json`.
- Para a animação gênio ficar limpa, desligue a animação nativa: `SystemPropertiesPerformance` → desmarque "Animar janelas ao minimizar e maximizar".

## Créditos

- [Vortice.Windows](https://github.com/amerkoleci/Vortice.Windows) (MIT) — acesso ao Direct3D 11, DXGI e DirectComposition.
- Visual inspirado no macOS; nenhum ícone, fonte ou logotipo da Apple é usado.
