/// <reference types="vite/client" />
interface ImportMetaEnv {
  /** public = landing + portal; backoffice = só backoffice; all = tudo (dev/teste). D-W03. */
  readonly VITE_APP_TARGET: 'public' | 'backoffice' | 'all';
}
