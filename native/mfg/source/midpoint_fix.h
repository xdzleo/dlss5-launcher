#pragma once

#include <Windows.h>

#include <cstdint>

namespace midpoint_fix
{
using LogCallback = void (*)(const wchar_t* message);

void SetLogCallback(LogCallback callback) noexcept;
bool ObserveD3D12Device(void* device) noexcept;

// RenoDX: confirma a placa sem depender de gancho nenhum, e sem encostar em DXGI.
//
// ObserveD3D12Device so e chamado pelo gancho de slSetD3DDevice, e esse gancho exige que ALGUEM
// no processo importe o Streamline por tabela de importacao. Em jogo Unreal quem carrega o
// Streamline e um plugin que resolve por GetProcAddress, e ai o gancho nunca entra -- a placa
// nunca era confirmada e a correcao D157 ficava desligada para sempre. Numa Ada isso e o pior
// desfecho possivel: os modos acima de 2x aparecem no menu e entregam quadros colapsados.
//
// Aqui a pergunta e respondida direto: quantas placas NVIDIA existem nesta maquina? Se houver
// exatamente uma, e ela que vai rodar o jogo (nao existe DLSS em outra), e a capacidade de
// computo dela decide. Mais de uma, ou nenhuma, e a resposta continua sendo nao.
bool VerifyAdapterFromSoleCudaDevice() noexcept;
bool PatchProvider(HMODULE module, const wchar_t* path) noexcept;
bool AdapterVerified() noexcept;
bool Ready() noexcept;
uint32_t FailureCode() noexcept;
}
