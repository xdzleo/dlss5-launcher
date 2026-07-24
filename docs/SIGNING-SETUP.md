# Configurar assinatura grátis (SignPath Foundation) — passo a passo

O pipeline (`.github/workflows/release.yml`) já está pronto. Faltam só os passos abaixo, que
**só você pode fazer** (envolvem criar conta e aceitar termos). Uma vez feito, todo release
assinado sai sozinho ao criar uma tag `vX.Y.Z`.

## 1. Enviar o projeto para o SignPath Foundation (aprovação deles — pode levar dias)

1. Acesse <https://signpath.org/apply> (programa gratuito para open-source).
2. Preencha com os dados do projeto:
   - Repositório: `https://github.com/xdzleo/renodx-launcher`
   - Licença: **MIT** (já está no repo, arquivo `LICENSE`)
   - Link da política de assinatura: `https://github.com/xdzleo/renodx-launcher/blob/master/docs/code-signing-policy.md`
3. Aguarde a aprovação da SignPath Foundation. Eles revisam manualmente.

> Requisitos que o projeto **já cumpre**: licença OSI (MIT), código público, mantido, já
> lançado, build feito por CI (GitHub Actions), MFA na conta, política de assinatura publicada.

## 2. Configurar a organização/projeto no painel SignPath.io

Depois de aprovado, no dashboard em <https://app.signpath.io>:

1. Anote o **Organization ID** (fica em *Settings*).
2. Crie um **Project** com o slug exato **`renodx-launcher`**.
3. No projeto, crie uma **Artifact Configuration** que descreva o zip:
   um `.zip` contendo `RenoDXLauncher.exe`, com Authenticode signing. (Pode deixar como
   configuração padrão do projeto.)
4. Crie uma **Signing Policy** com o slug exato **`release-signing`** usando o
   certificado da SignPath Foundation, com **aprovação manual** ligada.
5. Conecte o **Trusted Build System = GitHub Actions** e autorize o repositório.
6. Gere um **API Token** (guarde — você vai colar no passo 3).

> Se você usar slugs diferentes de `renodx-launcher` / `release-signing`, edite esses dois
> valores em `.github/workflows/release.yml`.

## 3. Colocar os segredos no GitHub

No repositório em **Settings → Secrets and variables → Actions**:

- Aba **Secrets** → *New repository secret*:
  - Nome: `SIGNPATH_API_TOKEN` — valor: o token do passo 2.6
- Aba **Variables** → *New repository variable*:
  - Nome: `SIGNPATH_ORGANIZATION_ID` — valor: o Organization ID do passo 2.1

## 4. Lançar um release assinado

```bash
git tag v1.1.0
git push origin v1.1.0
```

O workflow **Release** builda no CI, envia pro SignPath, você **aprova a assinatura** no
dashboard (chega notificação), e o zip **assinado** é anexado ao GitHub Release automaticamente.

## O que esperar

- O "publicador" no Windows vai aparecer como **SignPath Foundation** (não como você — é assim
  no programa gratuito).
- Assinar **não zera** mais a reputação a cada versão: ela **acumula** na identidade do
  certificado. Os avisos do SmartScreen/Chrome vão **diminuindo** conforme o número de
  downloads cresce (não é instantâneo — mito do EV "instantâneo" não existe mais).
- Enquanto a reputação esquenta, dá pra pedir revisão manual do arquivo assinado no
  [Microsoft Security Intelligence](https://www.microsoft.com/wdsi/filesubmission) e no
  [Google Safe Browsing](https://safebrowsing.google.com/safebrowsing/report_error/).
