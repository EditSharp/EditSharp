using System;
using System.Collections.Generic;
 
namespace EditSharp.Components.Nodes
{
    public abstract class Node
    {
        public Guid Id { get; } = Guid.NewGuid();
 
        //fixed set, declared by the concrete node type
        public abstract IReadOnlyList<NodePort> Ports { get; }
 
        //bypass — see Graph's class remarks
        public bool Enabled { get; set; } = true;
 
        /// <summary>
        /// Deep copy with a FRESH Id — a duplicated clip's graph must not
        /// share node identity with the original, or a Connection recorded
        /// against one graph could be mistaken for referencing a node in
        /// the other.
        /// </summary>
        public abstract Node Duplicate();
    }
}
 