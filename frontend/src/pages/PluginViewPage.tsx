import { useEffect, useState, useRef } from 'react';
import { useParams, useNavigate } from 'react-router-dom';
import { getPlugins, type PluginDefinition } from '../services/api';
import { ArrowLeft, ExternalLink, RefreshCw } from 'lucide-react';

const PluginViewPage = () => {
    const { id } = useParams();
    const navigate = useNavigate();
    const [plugin, setPlugin] = useState<PluginDefinition | null>(null);
    const [loading, setLoading] = useState(true);
    const iframeRef = useRef<HTMLIFrameElement>(null);

    useEffect(() => {
        if (!id) return;
        loadPlugin(+id);
    }, [id]);

    const loadPlugin = async (pid: number) => {
        try {
            const plugins = await getPlugins();
            const found = plugins.find(p => p.id === pid);
            if (found) setPlugin(found);
            else navigate('/apps'); // Not found
        } catch {
            navigate('/apps');
        } finally {
            setLoading(false);
        }
    };

    if (loading) return <div className="text-white p-10">Loading App...</div>;
    if (!plugin) return null;

    // Use Proxy URL
    const proxyUrl = `/api/plugins/${plugin.id}/proxy${plugin.entryPath}`;

    return (
        <div className="flex flex-col h-full bg-white">
            {/* Header Bar */}
            <div className="h-14 bg-gray-900 border-b border-gray-800 flex items-center justify-between px-4 text-white shrink-0">
                <div className="flex items-center gap-3">
                    <button onClick={() => navigate('/apps')} className="p-2 hover:bg-white/10 rounded-full text-gray-400 hover:text-white transition">
                        <ArrowLeft size={20} />
                    </button>
                    <div className="flex flex-col">
                        <span className="font-bold text-sm tracking-wide">{plugin.name}</span>
                        <span className="text-[10px] text-gray-500 font-mono">Proxy: {plugin.baseUrl}</span>
                    </div>
                </div>
                <div className="flex gap-2">
                    <button onClick={() => iframeRef.current?.contentWindow?.location.reload()} className="p-2 hover:bg-white/10 rounded-full text-gray-400 hover:text-white" title="Reload Frame">
                        <RefreshCw size={18} />
                    </button>
                    <a href={proxyUrl} target="_blank" rel="noreferrer" className="p-2 hover:bg-white/10 rounded-full text-gray-400 hover:text-white" title="Open in New Tab">
                        <ExternalLink size={18} />
                    </a>
                </div>
            </div>

            {/* IFrame Container */}
            <div className="flex-1 bg-gray-100 relative">
                <iframe
                    ref={iframeRef}
                    src={proxyUrl}
                    className="w-full h-full border-none"
                    title={plugin.name}
                    sandbox="allow-same-origin allow-scripts allow-forms allow-popups"
                />
            </div>
        </div>
    );
};

export default PluginViewPage;
